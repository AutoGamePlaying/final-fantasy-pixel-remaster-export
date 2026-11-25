#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
PHYS_ROOT="$(pwd -P)"
# Resolve native Windows path in Git Bash / MSYS when available so .NET env defaults stay valid.
HOST_ROOT="$(pwd -W 2>/dev/null || pwd)"

CI_LOG_FILE=""
cleanup_ci_log() {
  if [[ -n "$CI_LOG_FILE" && -f "$CI_LOG_FILE" ]]; then
    rm -f "$CI_LOG_FILE"
  fi
}

if [[ "${GITHUB_ACTIONS:-}" == "true" ]]; then
  trap cleanup_ci_log EXIT
fi

emit_ci_annotations() {
  local step="$1"
  local log_file="$2"
  local exit_code="$3"

  FFPR_CI_STEP="$step" \
  FFPR_CI_EXIT_CODE="$exit_code" \
  FFPR_CI_ROOT_1="$ROOT" \
  FFPR_CI_ROOT_2="$PHYS_ROOT" \
  FFPR_CI_ROOT_3="$HOST_ROOT" \
  FFPR_CI_ROOT_4="${GITHUB_WORKSPACE:-}" \
  LC_ALL=C awk '
    function trim(s) {
      sub(/^[ \t]+/, "", s)
      sub(/[ \t]+$/, "", s)
      return s
    }

    function starts_with(s, prefix) {
      return substr(s, 1, length(prefix)) == prefix
    }

    function collapse_spaces(s) {
      gsub(/\t/, " ", s)
      gsub(/  +/, " ", s)
      return trim(s)
    }

    function replace_literal(s, pat, rep,    idx, out, plen) {
      if (pat == "") return s
      plen = length(pat)
      out = ""
      while ((idx = index(s, pat)) > 0) {
        out = out substr(s, 1, idx - 1) rep
        s = substr(s, idx + plen)
      }
      return out s
    }

    function replace_literal_ci(s, pat, rep,    lpat, idx, out, plen) {
      if (pat == "") return s
      lpat = tolower(pat)
      plen = length(pat)
      out = ""
      while ((idx = index(tolower(s), lpat)) > 0) {
        out = out substr(s, 1, idx - 1) rep
        s = substr(s, idx + plen)
      }
      return out s
    }

    function add_pat(p, ci,    i) {
      if (length(p) <= 1) return
      for (i = 1; i <= n_pats; i++) {
        if (pats[i] == p && pat_ci[i] == ci) return
      }
      n_pats++
      pats[n_pats] = p
      pat_ci[n_pats] = ci
    }

    function register_root(raw,    slash, bs, bbs, c3) {
      if (raw == "") return
      while (length(raw) > 1 && (substr(raw, length(raw), 1) == "/" || substr(raw, length(raw), 1) == "\\")) {
        raw = substr(raw, 1, length(raw) - 1)
      }
      if (length(raw) <= 1) return

      # Strip optional Windows verbatim path prefix before generating all slash/escaped forms.
      if (substr(raw, 1, 4) == "\\\\?\\" || substr(raw, 1, 4) == "//?/") {
        raw = substr(raw, 5)
      }

      c3 = substr(raw, 3, 1)
      if (raw ~ /^[A-Za-z]:/ && (c3 == "/" || c3 == "\\")) {
        slash = replace_literal(raw, "\\", "/")
        bs = replace_literal(slash, "/", "\\")
        bbs = replace_literal(slash, "/", "\\\\")
        add_pat("\\\\\\\\?\\\\" bbs, 1)
        add_pat("\\\\?\\" bs, 1)
        add_pat("//?/" slash, 1)
        add_pat(bbs, 1)
        add_pat(bs, 1)
        add_pat(slash, 1)
      } else {
        add_pat(raw, 0)
      }
    }

    function sort_pats(    i, j, tmp_p, tmp_ci) {
      for (i = 2; i <= n_pats; i++) {
        tmp_p = pats[i]
        tmp_ci = pat_ci[i]
        j = i - 1
        while (j >= 1 && length(pats[j]) < length(tmp_p)) {
          pats[j + 1] = pats[j]
          pat_ci[j + 1] = pat_ci[j]
          j--
        }
        pats[j + 1] = tmp_p
        pat_ci[j + 1] = tmp_ci
      }
    }

    function sanitize(s,    i) {
      gsub(/\033\[[0-9;?]*[A-Za-z]/, "", s)
      gsub(/\r/, "", s)
      s = collapse_spaces(s)
      for (i = 1; i <= n_pats; i++) {
        if (pat_ci[i]) {
          s = replace_literal_ci(s, pats[i], "<repo>")
        } else {
          s = replace_literal(s, pats[i], "<repo>")
        }
      }
      return s
    }

    function escape_prop(s) {
      s = replace_literal(s, "%", "%25")
      s = replace_literal(s, "\r", "%0D")
      s = replace_literal(s, "\n", "%0A")
      s = replace_literal(s, ":", "%3A")
      s = replace_literal(s, ",", "%2C")
      return s
    }

    function escape_data(s) {
      s = replace_literal(s, "%", "%25")
      s = replace_literal(s, "\r", "%0D")
      s = replace_literal(s, "\n", "%0A")
      return s
    }

    function truncate_utf8(s, max_len,    cut, b) {
      if (length(s) <= max_len) return s
      if (max_len <= 3) return substr(s, 1, max_len)
      cut = max_len - 3
      # Back off across UTF-8 continuation bytes so multibyte characters are never split.
      while (cut > 0) {
        b = substr(s, cut + 1, 1)
        if (b >= "\200" && b <= "\277") {
          cut--
        } else {
          break
        }
      }
      s = substr(s, 1, cut)
      # Avoid leaving a partial %XX escape at the truncation point.
      if (length(s) >= 1 && substr(s, length(s), 1) == "%") {
        s = substr(s, 1, length(s) - 1)
      } else if (length(s) >= 2 && substr(s, length(s) - 1, 2) ~ /^%[0-9A-Fa-f]$/) {
        s = substr(s, 1, length(s) - 2)
      }
      return s "..."
    }

    function emit(msg,    prefix, budget, body, line) {
      if (emitted >= max_annotations) return
      msg = sanitize(msg)
      if (msg == "") return
      prefix = "::error title=" title_esc "::"
      budget = max_line_len - length(prefix)
      if (budget <= 0) return
      body = truncate_utf8(escape_data(msg), budget)
      line = prefix body
      if (line in seen) return
      seen[line] = 1
      emitted++
      print line
    }

    function register_dotnet_test(name) {
      if (name == "") return
      if (!(name in dn_seen)) {
        dn_seen[name] = 1
        dn_count++
        dn_order[dn_count] = name
      }
    }

    function is_dotnet_detail_line(s) {
      return starts_with(s, "Expected:") || starts_with(s, "Actual:") || \
             starts_with(s, "String:") || starts_with(s, "RegEx:") || \
             starts_with(s, "Not found:") || starts_with(s, "Found:") || \
             starts_with(s, "In value:") || starts_with(s, "Looking for:") || \
             starts_with(s, "Filter:") || starts_with(s, "Collection:")
    }

    function record_dotnet_line(name, line,    key) {
      if (name == "") return
      line = collapse_spaces(line)
      if (line == "") return
      if (dn_msg[name] == "") {
        dn_msg[name] = line
        return
      }
      if (is_dotnet_detail_line(line)) {
        key = name SUBSEP line
        if (key in dn_extra_seen) return
        dn_extra_seen[key] = 1
        if (dn_extra_cnt[name] < 2) {
          dn_extra[name] = (dn_extra[name] == "" ? line : dn_extra[name] " | " line)
          dn_extra_cnt[name]++
        }
      }
    }

    function is_repo_file(p) {
      return starts_with(p, "<repo>/") || starts_with(p, "<repo>\\") || \
             starts_with(p, "src/") || starts_with(p, "src\\") || \
             starts_with(p, "tests/") || starts_with(p, "tests\\")
    }

    function normalize_repo_loc(fpath, fline) {
      gsub(/\\/, "/", fpath)
      if (!starts_with(fpath, "<repo>/")) {
        fpath = "<repo>/" fpath
      }
      return fpath ":" fline
    }

    function extract_stack_repo_loc(raw,    s, idx, rest, fpath, fline) {
      s = sanitize(raw)
      if (s == "") return ""

      # .NET / VSTest stack frame: "... in <path>:line <num>"
      if (match(s, /:line [0-9]+$/)) {
        fline = substr(s, RSTART + 6, RLENGTH - 6)
        rest = substr(s, 1, RSTART - 1)
        idx = index(rest, " in <repo>")
        if (idx > 0) {
          fpath = substr(rest, idx + 4)
          if (is_repo_file(fpath)) {
            return normalize_repo_loc(fpath, fline)
          }
        }
        idx = index(rest, " in src/")
        if (idx == 0) idx = index(rest, " in src\\")
        if (idx == 0) idx = index(rest, " in tests/")
        if (idx == 0) idx = index(rest, " in tests\\")
        if (idx > 0) {
          fpath = substr(rest, idx + 4)
          if (is_repo_file(fpath)) {
            return normalize_repo_loc(fpath, fline)
          }
        }
      }

      # xUnit runner stack frame: "<path>(<line>,<col>): at ..."
      if (match(s, /\([0-9]+(,[0-9]+)?\):[ \t]+/)) {
        fpath = substr(s, 1, RSTART - 1)
        fline = substr(s, RSTART + 1, RLENGTH - 1)
        sub(/[^0-9].*$/, "", fline)
        if (is_repo_file(fpath)) {
          return normalize_repo_loc(fpath, fline)
        }
      }

      return ""
    }

    function record_dotnet_stack(name, line,    loc) {
      if (name == "" || dn_loc[name] != "") return
      loc = extract_stack_repo_loc(line)
      if (loc != "") {
        dn_loc[name] = loc
      }
    }

    function emit_dotnet_tests(    i, name, body) {
      for (i = 1; i <= dn_count; i++) {
        name = dn_order[i]
        body = name
        if (dn_loc[name] != "") {
          body = body " | " dn_loc[name]
        }
        if (dn_msg[name] != "") {
          body = body " | " dn_msg[name]
          if (dn_extra[name] != "") {
            body = body " | " dn_extra[name]
          }
        } else {
          body = body " | test failed"
        }
        emit(body)
      }
    }

    BEGIN {
      max_annotations = 20
      max_line_len = 300
      emitted = 0
      n_pats = 0
      dn_count = 0
      dn_mode = ""
      dn_cur = ""
      dn_section = ""
      title_esc = escape_prop(ENVIRON["FFPR_CI_STEP"])
      exit_code = ENVIRON["FFPR_CI_EXIT_CODE"]

      register_root(ENVIRON["FFPR_CI_ROOT_1"])
      register_root(ENVIRON["FFPR_CI_ROOT_2"])
      register_root(ENVIRON["FFPR_CI_ROOT_3"])
      register_root(ENVIRON["FFPR_CI_ROOT_4"])
      sort_pats()
    }

    {
      raw_line = $0
      gsub(/\033\[[0-9;?]*[A-Za-z]/, "", raw_line)
      gsub(/\r/, "", raw_line)
      tline = trim(raw_line)
      if (tline != "") last_nonempty = tline

      # xUnit test failure header (may be followed inline by Assert/exception lines and Stack Trace).
      dline = raw_line
      sub(/^[ \t]*\[xUnit\.net [^]]*\][ \t]*/, "", dline)
      dline = trim(dline)
      if (dline ~ /\[FAIL\]$/ || dline ~ /^\[FAIL\][ \t]+/) {
        dotnet_test = dline
        sub(/[ \t]*\[FAIL\]$/, "", dotnet_test)
        sub(/^\[FAIL\][ \t]+/, "", dotnet_test)
        dotnet_test = trim(dotnet_test)
        register_dotnet_test(dotnet_test)
        dn_mode = "xunit"
        dn_cur = dotnet_test
        dn_section = "msg"
        next
      }

      # VSTest detail header ("  Failed <TestName> [<duration>]") emitted with Error Message and Stack Trace.
      if (raw_line ~ /^[ \t]+Failed .+ \[[^]]+\]$/) {
        vtest = raw_line
        sub(/^[ \t]+Failed /, "", vtest)
        sub(/ \[[^]]+\]$/, "", vtest)
        vtest = trim(vtest)
        register_dotnet_test(vtest)
        dn_mode = "vstest"
        dn_cur = vtest
        dn_section = ""
        next
      }

      if (dn_mode == "xunit") {
        if (raw_line ~ /^[ \t]*(Passed!|Failed!|Total tests:|Test Run Failed|Starting test execution)/ ||
            dline ~ /\[(SKIP|PASS)\]$/ ||
            dline ~ /^(Discovering|Discovered|Starting|Finished):[ \t]/) {
          dn_mode = ""
          dn_section = ""
        } else if (raw_line ~ /^[ \t]*\[xUnit\.net /) {
          if (starts_with(dline, "Stack Trace:")) {
            dn_section = "stack"
          } else if (starts_with(dline, "Output:")) {
            dn_section = "output"
          } else if (dn_section == "msg") {
            record_dotnet_line(dn_cur, dline)
          } else if (dn_section == "stack") {
            record_dotnet_stack(dn_cur, dline)
          }
          next
        } else {
          dn_mode = ""
          dn_section = ""
        }
      }

      if (dn_mode == "vstest") {
        if (raw_line ~ /^[ \t]*(Passed!|Failed!|Total tests:|Test Run Failed|Starting test execution|Passed[ \t]+|Skipped[ \t]+)/) {
          dn_mode = ""
          dn_section = ""
        } else if (tline == "Error Message:") {
          dn_section = "msg"
          next
        } else if (tline == "Stack Trace:") {
          dn_section = "stack"
          next
        } else if (starts_with(tline, "Standard Output Messages:") || starts_with(tline, "Standard Error Messages:")) {
          dn_section = "output"
          next
        } else if (dn_section == "msg") {
          record_dotnet_line(dn_cur, tline)
          next
        } else if (dn_section == "stack") {
          record_dotnet_stack(dn_cur, tline)
          next
        } else if (dn_section == "output") {
          next
        }
      }

      # MSBuild / Roslyn C# compiler errors (e.g. error CS..., error MSB..., error NU...).
      if (tline ~ /(^|:)[ \t]*error [A-Za-z]+[0-9]+[ \t]*:/ || tline ~ /:[ \t]*error[ \t]*:/) {
        cs_err = tline
        sub(/[ \t]+\[[^][]+\.(csproj|sln)(::[^][]*)?\]$/, "", cs_err)
        emit(cs_err)
        next
      }
    }

    END {
      emit_dotnet_tests()
      if (emitted == 0) {
        if (last_nonempty != "") {
          emit("exit code " exit_code " | " last_nonempty)
        } else {
          emit("exit code " exit_code)
        }
      }
    }
  ' "$log_file"
}

run_ci_logged() {
  local step="$1"
  shift

  if [[ "${GITHUB_ACTIONS:-}" != "true" ]]; then
    "$@"
    return $?
  fi

  local tmp_base="${TMPDIR:-/tmp}"
  tmp_base="${tmp_base%/}"
  CI_LOG_FILE="$(mktemp "${tmp_base}/ffpr-check-log.XXXXXX")"
  local rc=0
  set +e
  "$@" 2>&1 | tee "$CI_LOG_FILE"
  rc="${PIPESTATUS[0]}"
  set -e

  if [[ "$rc" -ne 0 ]]; then
    emit_ci_annotations "$step" "$CI_LOG_FILE" "$rc" || true
    rm -f "$CI_LOG_FILE"
    CI_LOG_FILE=""
    exit "$rc"
  fi

  rm -f "$CI_LOG_FILE"
  CI_LOG_FILE=""
}

if [[ -n "${DOTNET_ROOT:-}" ]]; then
  DOTNET_DIR="${DOTNET_ROOT%/}"
  DOTNET_DIR="${DOTNET_DIR%\\}"
  if [[ -x "$DOTNET_DIR/dotnet.exe" ]]; then
    DOTNET_BIN="$DOTNET_DIR/dotnet.exe"
  else
    DOTNET_BIN="$DOTNET_DIR/dotnet"
  fi
elif [[ -x "$ROOT/.deps/dotnet/dotnet" ]]; then
  export DOTNET_ROOT="$HOST_ROOT/.deps/dotnet"
  DOTNET_BIN="$ROOT/.deps/dotnet/dotnet"
elif [[ -x "$ROOT/.deps/dotnet/dotnet.exe" ]]; then
  export DOTNET_ROOT="$HOST_ROOT/.deps/dotnet"
  DOTNET_BIN="$ROOT/.deps/dotnet/dotnet.exe"
elif command -v dotnet >/dev/null 2>&1; then
  DOTNET_BIN="$(command -v dotnet)"
else
  export DOTNET_ROOT="$HOST_ROOT/.deps/dotnet"
  DOTNET_BIN="$ROOT/.deps/dotnet/dotnet"
fi

if [[ ! -x "$DOTNET_BIN" ]]; then
  echo "错误：找不到 dotnet 可执行文件：$DOTNET_BIN（请安装 .NET 10 SDK 到 .deps/dotnet 或设置 DOTNET_ROOT）" >&2
  exit 2
fi

export DOTNET_CLI_HOME="${DOTNET_CLI_HOME:-$HOST_ROOT/.deps/dotnet-home}"
export NUGET_PACKAGES="${NUGET_PACKAGES:-$HOST_ROOT/.deps/nuget}"
export NUGET_HTTP_CACHE_PATH="${NUGET_HTTP_CACHE_PATH:-$HOST_ROOT/.deps/nuget-http}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
# Do not leave MSBuild / compiler server processes running after the script exits: they would
# inherit file descriptors such as the shared `flock /tmp/ffpr-heavy.lock` and hold it forever.
export MSBUILDDISABLENODEREUSE=1
export DOTNET_CLI_USE_MSBUILD_SERVER=0
export UseSharedCompilation=false

# Format checks run on Linux only to avoid Windows line-ending noise in CI.
SKIP_FMT="${FFPR_CHECK_SKIP_FMT:-0}"
case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*)
    SKIP_FMT=1
    ;;
esac

if [[ "$SKIP_FMT" == "0" ]]; then
  echo "==> dotnet format --verify-no-changes"
  "$DOTNET_BIN" format FfprExport.sln --verify-no-changes
else
  echo "==> 跳过 dotnet format（非 Linux 环境或已设置 FFPR_CHECK_SKIP_FMT）"
fi

echo "==> dotnet build -c Release"
run_ci_logged "dotnet build" "$DOTNET_BIN" build FfprExport.sln -c Release

echo "==> dotnet test"
run_ci_logged "dotnet test" "$DOTNET_BIN" test FfprExport.sln -c Release --no-build

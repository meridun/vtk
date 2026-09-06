// vtk-faketool — canned external tools for the end-to-end smoke suite.
//
// The deleted Go smoke suite (test/smoke/*, pre-#59) compiled a small fake
// binary per wrapped tool at test time so vtk could be exercised end-to-end
// (PATH lookup, capture, filter, savings gate, spool, exit-code parity)
// without a live eslint/gh/mocha/npm/dbmate install. This is the C# analog:
// one binary emulating every fake, dispatched via `--as <tool>` (the smoke
// harness drops a `<tool>.cmd` shim into a scratch bin dir that execs
// `dotnet vtk-faketool.dll --as <tool> %*`).
//
// Exit codes are forced through the same VTK_FAKE_<TOOL>_CODE env vars the Go
// fakes used. Payload shapes mirror the Go fakes / captured real output, but
// several are enlarged relative to the Go originals: those predate the #52
// savings gate (spool + `OK <id>` fire only when compaction saves >= 256
// bytes AND >= 20%), and the smoke assertions on `OK <id>` need payloads
// whose compaction clears that bar.
using System.Text;

namespace Vtk.FakeTool;

public static class Program
{
    public static int Main(string[] rawArgs)
    {
        // Deterministic bytes on every sink: UTF-8, "\n" newlines.
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var stderr = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };

        if (rawArgs.Length < 2 || rawArgs[0] != "--as")
        {
            stderr.WriteLine("usage: vtk-faketool --as <tool> [tool args...]");
            return 64;
        }
        var tool = rawArgs[1].ToLowerInvariant();
        var args = rawArgs[2..];

        return tool switch
        {
            "eslint" => Eslint(stdout, stderr),
            "mocha" => Mocha(stdout, stderr, banner: null),
            "cross-env" => CrossEnv(stdout, stderr, args),
            "npx" => Npx(stdout, stderr, args),
            "npm" => Npm(stdout, stderr, args),
            "gh" => Gh(stdout, stderr, args),
            "powershell" or "pwsh" => Powershell(stdout, stderr, args),
            "dbmate" => Dbmate(stdout, stderr, args),
            "ls" => Ls(stdout, args),
            "grep" => Grep(stdout, args),
            "find" => Find(stdout),
            "cat" => Cat(),
            _ => Unknown(stderr, tool),
        };
    }

    private static int Unknown(TextWriter stderr, string tool)
    {
        stderr.WriteLine($"vtk-faketool: unknown tool {tool}");
        return 64;
    }

    private static int EnvCode(string name, int dflt)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return int.TryParse(v, out var n) ? n : dflt;
    }

    // ---- eslint ------------------------------------------------------------
    // VTK_FAKE_ESLINT_CODE (default 1): 0 -> clean run, no output;
    // 1 -> problems report on stdout (the case vtk compacts);
    // 2 -> fatal/config error on stderr, no report (must stay raw).
    private static int Eslint(TextWriter stdout, TextWriter stderr)
    {
        var code = EnvCode("VTK_FAKE_ESLINT_CODE", 1);
        switch (code)
        {
            case 0:
                break; // clean run: eslint prints nothing
            case 2:
                stderr.WriteLine("Oops! Something went wrong! :(");
                stderr.WriteLine("ESLint: 9.0.0");
                stderr.WriteLine("Error: Cannot read config file: .eslintrc.json");
                break;
            default:
                stdout.Write(EslintReport);
                break;
        }
        return code;
    }

    // Stylish-format problems report. Rollup: 7x error semi, 3x error
    // no-undef, 1x warning no-unused-vars, 1x warning no-console.
    private const string EslintReport =
        "\nsrc/app.js\n" +
        "   1:1   error    'foo' is not defined                  no-undef\n" +
        "   2:10  error    Missing semicolon                     semi\n" +
        "   5:1   warning  'bar' is assigned a value but never used  no-unused-vars\n" +
        "  12:7   error    Missing semicolon                     semi\n" +
        "  14:2   error    Missing semicolon                     semi\n" +
        "  18:9   error    'qux' is not defined                  no-undef\n" +
        "  21:4   error    Missing semicolon                     semi\n" +
        "\nsrc/util.js\n" +
        "   3:5   error    Missing semicolon                     semi\n" +
        "   8:1   warning  Unexpected console statement          no-console\n" +
        "  14:3   error    'baz' is not defined                  no-undef\n" +
        "  17:6   error    Missing semicolon                     semi\n" +
        "  22:11  error    Missing semicolon                     semi\n" +
        "\n✖ 12 problems (10 errors, 2 warnings)\n";

    // ---- cross-env ---------------------------------------------------------
    // `cross-env VAR=x [...] <cmd> [args...]`: applies the assignments to the
    // process environment, then runs the inner fake in-process — mirroring
    // real cross-env's exec of the trailing command untouched (#97). Because
    // assignments are real env writes, VTK_FAKE_*_CODE can be set through
    // cross-env itself, exactly like real traffic.
    private static int CrossEnv(TextWriter stdout, TextWriter stderr, string[] args)
    {
        var i = 0;
        while (i < args.Length && System.Text.RegularExpressions.Regex.IsMatch(args[i], "^[A-Za-z_][A-Za-z0-9_]*="))
        {
            var eq = args[i].IndexOf('=');
            Environment.SetEnvironmentVariable(args[i][..eq], args[i][(eq + 1)..]);
            i++;
        }
        if (i >= args.Length)
        {
            stderr.WriteLine("vtk-faketool cross-env: missing command");
            return 64;
        }
        return args[i] switch
        {
            "mocha" => Mocha(stdout, stderr, banner: null),
            "eslint" => Eslint(stdout, stderr),
            _ => Unknown(stderr, args[i]),
        };
    }

    // ---- mocha / npx -------------------------------------------------------
    // VTK_FAKE_MOCHA_CODE (default 1) selects the exit code AND the payload:
    // 0 -> green run; N>0 -> failing run at exit N (real mocha exits its
    // failure count, min(failures, 255); every 0..255 code is allowlisted,
    // #138). VTK_FAKE_MOCHA_FATAL=1 instead emits a real "No test files
    // found" config error on stderr — no summary line — at exit 1: the
    // content gate must keep it raw.
    private static int Mocha(TextWriter stdout, TextWriter stderr, string? banner)
    {
        if (EnvCode("VTK_FAKE_MOCHA_FATAL", 0) == 1)
        {
            stderr.Write(MochaFatalRaw);
            return 1;
        }
        var code = EnvCode("VTK_FAKE_MOCHA_CODE", 1);
        if (banner != null) stdout.Write(banner);
        stdout.Write(code == 0 ? MochaPassRaw : MochaFailRaw);
        return code;
    }

    // Captured from mocha 11.7.5 with a glob matching nothing (exit 1).
    private const string MochaFatalRaw =
        "\u001b[31mError: No test files found: \"nope/**/*.test.js\"\u001b[39m\n";

    // `npx <tool>`: the Go suite copied the fake mocha to npx(.exe) so that
    // `npx mocha` resolved to it. Emulate the same, tolerating the
    // transparent flags real npx traffic carries (#97): mocha and eslint are
    // wired; anything else (including non-transparent flags like -p) is the
    // unknown-inner error path.
    private static int Npx(TextWriter stdout, TextWriter stderr, string[] args)
    {
        var i = 0;
        while (i < args.Length && args[i] is "--yes" or "-y" or "--no-install") i++;
        if (i < args.Length && args[i] == "mocha")
            return Mocha(stdout, stderr, banner: null);
        if (i < args.Length && args[i] == "eslint")
            return Eslint(stdout, stderr);
        stderr.WriteLine("vtk-faketool npx: unknown inner tool");
        return 1;
    }

    private const string MochaFailRaw =
        "\n\n" +
        "  cart\n" +
        "    ✔ starts empty\n" +
        "    ✔ adds an item\n" +
        "    1) computes total\n" +
        "    2) applies discount\n" +
        "    ✔ is serializable\n" +
        "\n" +
        "  checkout\n" +
        "    ✔ validates address\n" +
        "    3) charges card\n" +
        "    ✔ sends receipt\n" +
        "\n" +
        "  inventory\n" +
        "    ✔ tracks stock levels for item 01\n" +
        "    ✔ tracks stock levels for item 02\n" +
        "    ✔ tracks stock levels for item 03\n" +
        "    ✔ tracks stock levels for item 04\n" +
        "    ✔ tracks stock levels for item 05\n" +
        "    ✔ tracks stock levels for item 06\n" +
        "    ✔ tracks stock levels for item 07\n" +
        "    ✔ tracks stock levels for item 08\n" +
        "    ✔ tracks stock levels for item 09\n" +
        "    ✔ tracks stock levels for item 10\n" +
        "    ✔ tracks stock levels for item 11\n" +
        "    ✔ tracks stock levels for item 12\n" +
        "    ✔ tracks stock levels for item 13\n" +
        "    ✔ tracks stock levels for item 14\n" +
        "    ✔ tracks stock levels for item 15\n" +
        "    ✔ tracks stock levels for item 16\n" +
        "    ✔ tracks stock levels for item 17\n" +
        "    ✔ tracks stock levels for item 18\n" +
        "    ✔ tracks stock levels for item 19\n" +
        "    ✔ tracks stock levels for item 20\n" +
        "\n\n" +
        "  25 passing (6ms)\n" +
        "  3 failing\n" +
        "\n" +
        "  1) cart\n" +
        "       computes total:\n" +
        "\n" +
        "      AssertionError [ERR_ASSERTION]: 12 == 11\n" +
        "      + expected - actual\n" +
        "\n" +
        "      -12\n" +
        "      +11\n" +
        "      \n" +
        "      at Context.<anonymous> (test\\mixed.test.js:5:45)\n" +
        "      at process.processImmediate (node:internal/timers:485:21)\n" +
        "\n" +
        "  2) cart\n" +
        "       applies discount:\n" +
        "\n" +
        "      AssertionError [ERR_ASSERTION]: 90 == 80\n" +
        "      + expected - actual\n" +
        "\n" +
        "      -90\n" +
        "      +80\n" +
        "      \n" +
        "      at Context.<anonymous> (test\\mixed.test.js:6:47)\n" +
        "      at process.processImmediate (node:internal/timers:485:21)\n" +
        "\n" +
        "  3) checkout\n" +
        "       charges card:\n" +
        "     Error: gateway timeout\n" +
        "      at Context.<anonymous> (test\\mixed.test.js:11:42)\n" +
        "      at process.processImmediate (node:internal/timers:485:21)\n" +
        "\n\n\n";

    private const string MochaPassRaw =
        "\n\n" +
        "  math\n" +
        "    addition\n" +
        "      ✔ adds small numbers\n" +
        "      ✔ adds zero\n" +
        "      ✔ is commutative\n" +
        "    subtraction\n" +
        "      ✔ subtracts\n" +
        "      ✔ handles negatives\n" +
        "\n" +
        "  strings\n" +
        "    ✔ concatenates\n" +
        "    ✔ uppercases\n" +
        "\n" +
        "  inventory\n" +
        "    ✔ tracks stock levels for item 01\n" +
        "    ✔ tracks stock levels for item 02\n" +
        "    ✔ tracks stock levels for item 03\n" +
        "    ✔ tracks stock levels for item 04\n" +
        "    ✔ tracks stock levels for item 05\n" +
        "    ✔ tracks stock levels for item 06\n" +
        "    ✔ tracks stock levels for item 07\n" +
        "    ✔ tracks stock levels for item 08\n" +
        "    ✔ tracks stock levels for item 09\n" +
        "    ✔ tracks stock levels for item 10\n" +
        "    ✔ tracks stock levels for item 11\n" +
        "    ✔ tracks stock levels for item 12\n" +
        "    ✔ tracks stock levels for item 13\n" +
        "    ✔ tracks stock levels for item 14\n" +
        "    ✔ tracks stock levels for item 15\n" +
        "    ✔ tracks stock levels for item 16\n" +
        "    ✔ tracks stock levels for item 17\n" +
        "    ✔ tracks stock levels for item 18\n" +
        "    ✔ tracks stock levels for item 19\n" +
        "    ✔ tracks stock levels for item 20\n" +
        "\n\n" +
        "  27 passing (5ms)\n\n";

    // ---- npm ---------------------------------------------------------------
    // Emulates `npm run <script>`: the two-line npm banner then the inner
    // tool's output. Script selects the inner shape:
    //   lint      -> eslint problems report (delegates to the eslint filter;
    //                exit from VTK_FAKE_NPM_CODE, default 1)
    //   nofil     -> plain output with no inner filter (banner-stripped
    //                passthrough, gap attributed to node; exit default 0)
    //   test      -> mocha spec run (exit/payload from VTK_FAKE_MOCHA_CODE)
    //   itest     -> mocha spec run behind a cross-env banner line (#97, the
    //                telemetry shape `> cross-env INTEGRATION=1 mocha ...`;
    //                exit/payload from VTK_FAKE_MOCHA_CODE)
    //   db:status -> dbmate status report (exit 0)
    //   bigraw    -> NO banner (npm >= 11 suppresses it off-TTY, #93) and a
    //                large (>64KB) plain payload: the size-floored fold shape
    //                (exit from VTK_FAKE_NPM_CODE, default 0)
    //   quiet     -> NO banner and a terse status payload (`npm run sdlc`
    //                shape): must stay a byte-identical inline passthrough
    //                (exit from VTK_FAKE_NPM_CODE, default 0)
    //   bignoise  -> NO banner and a large (>64KB) mocha-shaped spec run
    //                whose "N passing" summary is followed by leftover-timer
    //                console noise: the #134 shape (the summary sits above
    //                the positional fold tail; exit from VTK_FAKE_NPM_CODE,
    //                default 0)
    //
    // Lifecycle aliases (`npm test`, `npm t`, `npm tst`, `npm start`, `npm
    // stop`, `npm restart`) run scripts without the `run` verb (#120): they
    // map to the script of the same name (t/tst normalize to test), or to
    // VTK_FAKE_NPM_ALIAS_SCRIPT when set — which lets a smoke test reach the
    // banner-less shapes (bigraw/quiet) through an alias spelling.
    private static int Npm(TextWriter stdout, TextWriter stderr, string[] args)
    {
        var script = args.Length >= 2 && args[0] == "run" ? args[1]
            : args.Length >= 1 && args[0] is "test" or "t" or "tst" or "start" or "stop" or "restart"
                ? Environment.GetEnvironmentVariable("VTK_FAKE_NPM_ALIAS_SCRIPT")
                    ?? (args[0] is "t" or "tst" ? "test" : args[0])
                : "";
        switch (script)
        {
            case "lint":
                stdout.Write("> demo@1.0.0 lint\n> eslint . --cache\n\n");
                stdout.Write(EslintReport);
                return EnvCode("VTK_FAKE_NPM_CODE", 1);
            case "nofil":
                stdout.Write("> demo@1.0.0 nofil\n> node scripts/x.mjs --check\n\n");
                stdout.Write("config is in sync (12 files checked)\n");
                return EnvCode("VTK_FAKE_NPM_CODE", 0);
            case "bigraw":
                // Banner-less bulk: ~90KB of per-item progress lines with a
                // summary block at the end (the tail a fold keeps inline).
                for (var i = 1; i <= 1500; i++)
                    stdout.Write($"processed item {i:0000} of 1500: synthesized payload row with filler columns\n");
                stdout.Write("\nall items processed\ndone: 1500 items in 4.2s\n");
                return EnvCode("VTK_FAKE_NPM_CODE", 0);
            case "quiet":
                stdout.Write("sdlc: verify 60 -> ADVANCE (audit)\nsdlc: 1 lane processed\n");
                return EnvCode("VTK_FAKE_NPM_CODE", 0);
            case "bignoise":
                // Banner-less mocha spec bulk, then the summary, then five
                // after-hook timer lines that log after mocha's summary.
                stdout.Write("\n\n  vtk #134 shape\n");
                for (var i = 1; i <= 1500; i++)
                    stdout.Write($"    ✔ case {i:0000} persists the resulting state change through the repository layer\n");
                stdout.Write("\n\n  1500 passing (58ms)\n\n");
                for (var i = 0; i < 5; i++)
                    stdout.Write("[TraderQueryAction.complete] No socket to emit response\n");
                return EnvCode("VTK_FAKE_NPM_CODE", 0);
            case "test":
                return Mocha(stdout, stderr, banner: "> demo@1.0.0 test\n> mocha --reporter spec\n\n");
            case "itest":
                return Mocha(stdout, stderr, banner: "> demo@1.0.0 itest\n> cross-env INTEGRATION=1 mocha --reporter spec\n\n");
            case "db:status":
                stdout.Write("> demo@1.0.0 db:status\n> dbmate status\n\n");
                stdout.Write(DbmateStatusOut);
                return 0;
            default:
                stderr.WriteLine("unknown script");
                return 1;
        }
    }

    // ---- gh ----------------------------------------------------------------
    // Canned non-TTY `gh` output keyed on argv (shapes mirror real gh piped
    // output). Exit code forced via VTK_FAKE_GH_CODE (default 0).
    private static int Gh(TextWriter stdout, TextWriter stderr, string[] args)
    {
        var code = EnvCode("VTK_FAKE_GH_CODE", 0);
        var hasJson = args.Contains("--json");
        var sub = args.Length >= 1 ? args[0] : "";
        var verb = args.Length >= 2 ? args[1] : "";

        if (hasJson)
        {
            stdout.Write("[{\"number\":112,\"state\":\"OPEN\",\"title\":\"Fix the flux capacitor\"}," +
                         "{\"number\":111,\"state\":\"OPEN\",\"title\":\"Add a new coverage filter\"}]\n");
            return code;
        }
        switch (sub, verb)
        {
            case ("issue", "list"):
                // 20 rows; labels + timestamp columns are what the filter
                // drops, so they carry the bulk that clears the savings bar.
                for (var i = 120; i > 100; i--)
                {
                    var state = i % 4 == 0 ? "CLOSED" : "OPEN";
                    stdout.Write($"{i}\t{state}\tTicket {i}: improve the flux capacitor coverage in module {i}\tbug,priority:high,needs-triage,area-filters\t2026-07-01T10:00:00Z\n");
                }
                return code;
            case ("issue", "view"):
                stdout.Write("title:\tFix the flux capacitor\n" +
                             "state:\tOPEN\n" +
                             "author:\tmeridun\n" +
                             "labels:\tbug, urgent\n" +
                             "--\n" +
                             "The capacitor fluxes intermittently under load.\n");
                return code;
            case ("pr", "list"):
                for (var i = 60; i > 50; i--)
                {
                    var state = i % 3 == 0 ? "MERGED" : "OPEN";
                    stdout.Write($"{i}\tWire up coverage for the number {i} filter family\tfeat/{i}-some-longish-branch-name-for-padding\t{state}\t2026-07-05T09:00:00Z\n");
                }
                return code;
            case ("run", "list"):
                stdout.Write("completed\tsuccess\tCI\tbuild.yml\tmain\tpush\t7788990011\t45s\t2026-07-06T11:00:00Z\n");
                stdout.Write("in_progress\t\tCI\tbuild.yml\tfeat/9\tpush\t7788990012\t\t2026-07-06T11:30:00Z\n");
                for (var i = 0; i < 8; i++)
                {
                    stdout.Write($"completed\tsuccess\tCI\tbuild.yml\tmain\tpush\t778899100{i}\t45s\t2026-07-06T1{i}:00:00Z\n");
                }
                return code;
            default:
                stderr.WriteLine("unknown fake gh args: " + string.Join(' ', args));
                return code;
        }
    }

    // ---- powershell / pwsh -------------------------------------------------
    // Emulates `powershell -ExecutionPolicy Bypass -File <script>` (#131):
    // the script path's stem selects the payload shape. Exit code forced via
    // VTK_FAKE_PS_CODE (default 0); the payload is unchanged by the code,
    // mirroring a test-runner script whose output precedes the failing exit.
    //   big.ps1   -> large (>64KB) e2e-runner-style payload (server log
    //                lines interleaved with runner progress) ending in a
    //                summary block: the size-floored fold shape
    //   quiet.ps1 -> terse status lines: must stay a byte-identical inline
    //                passthrough
    private static int Powershell(TextWriter stdout, TextWriter stderr, string[] args)
    {
        var script = "";
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("-File", StringComparison.OrdinalIgnoreCase))
            {
                script = Path.GetFileNameWithoutExtension(args[i + 1]);
                break;
            }
        }
        switch (script)
        {
            case "big":
                stdout.Write("run-e2e-local: starting server (pid 4242)\n");
                for (var i = 1; i <= 1200; i++)
                    stdout.Write($"[server] 2026-08-20T12:00:00.000Z GET /api/state 200 12ms request {i:0000} handled\n");
                stdout.Write("\n42 passed (312.4s)\nrun-e2e-local: done\n");
                return EnvCode("VTK_FAKE_PS_CODE", 0);
            case "quiet":
                stdout.Write("run-check: 3 files verified\nrun-check: ok\n");
                return EnvCode("VTK_FAKE_PS_CODE", 0);
            default:
                stderr.WriteLine("vtk-faketool powershell: unknown script");
                return 64;
        }
    }

    // ---- dbmate ------------------------------------------------------------
    // Emulates dbmate 2.27.0 (sqlite) human output. `status`/`up`/`rollback`
    // are report shapes (exit 0); `fail` writes an Error: line on stderr and
    // exits VTK_FAKE_DBMATE_CODE (default 1).
    private static int Dbmate(TextWriter stdout, TextWriter stderr, string[] args)
    {
        var sub = args.Length >= 1 ? args[0] : "";
        switch (sub)
        {
            case "status":
                stdout.Write(DbmateStatusOut);
                return 0;
            case "up":
                for (var i = 11; i <= 20; i++)
                {
                    stdout.Write($"Applying: 202601010000{i}_migration_step_{i}.sql\n");
                    stdout.Write($"Applied: 202601010000{i}_migration_step_{i}.sql in 2.005{i % 10}ms\n");
                }
                return 0;
            case "rollback":
                for (var i = 20; i >= 15; i--)
                {
                    stdout.Write($"Rolling back: 202601010000{i}_migration_step_{i}.sql\n");
                    stdout.Write($"Rolled back: 202601010000{i}_migration_step_{i}.sql in 2.004{i % 10}ms\n");
                }
                return 0;
            case "fail":
                stderr.Write("Applying: 20260101000019_add_read_flag.sql\n");
                stderr.Write("Error: table users already exists\n");
                return EnvCode("VTK_FAKE_DBMATE_CODE", 1);
            default:
                stderr.WriteLine("unknown command");
                return 1;
        }
    }

    private static string DbmateStatusOut
    {
        get
        {
            var sb = new StringBuilder();
            string[] names =
            {
                "create_users", "create_posts", "add_index", "create_sessions",
                "add_email_col", "create_orders", "add_orders_idx", "create_products",
                "create_carts", "add_cart_fk", "create_reviews", "add_review_rating",
                "create_tags", "create_post_tags", "add_slug", "create_media",
                "add_media_type", "create_notifications",
            };
            for (var i = 0; i < names.Length; i++)
                sb.Append($"[X] 202601010000{i + 1:00}_{names[i]}.sql\n");
            sb.Append("[ ] 20260101000019_add_read_flag.sql\n");
            sb.Append("[ ] 20260101000020_create_audit_log.sql\n");
            sb.Append("\nApplied: 18\nPending: 2\n");
            return sb.ToString();
        }
    }

    // ---- files family ------------------------------------------------------
    // Canned listings in the shapes the deleted Go smoke built out of real
    // directories: a large flat dir for ls/find (cap = 40 entries), grep -rn
    // with one over-cap file (cap = 5 matches/file), grep -rl with an
    // over-cap file list, and a no-match grep (exit 1, empty output).
    private static int Ls(TextWriter stdout, string[] args)
    {
        if (args.Contains("-l"))
        {
            stdout.Write("total 8\n");
            stdout.Write("-rw-r--r-- 1 smoke smoke  512 Jul  1 10:00 notes_a.txt\n");
            stdout.Write("-rw-r--r-- 1 smoke smoke  128 Jul  1 10:00 notes_b.txt\n");
            return 0;
        }
        for (var i = 1; i <= 160; i++)
            stdout.Write($"entry{i:000}.txt\n");
        return 0;
    }

    private static int Grep(TextWriter stdout, string[] args)
    {
        if (args.Contains("zzz-absent"))
            return 1; // no matches: grep exits 1 with no output

        if (args.Contains("-rl"))
        {
            for (var i = 1; i <= 200; i++)
                stdout.Write($"./m{i:000}.txt\n");
            return 0;
        }

        // grep -rn shape: notes_a over the per-file cap, notes_b under it.
        for (var i = 1; i <= 30; i++)
            stdout.Write($"./notes_a.txt:{i}:needle mark {i:00} with trailing filler content padding\n");
        stdout.Write("./notes_b.txt:1:needle b 1\n");
        stdout.Write("./notes_b.txt:2:needle b 2\n");
        return 0;
    }

    private static int Find(TextWriter stdout)
    {
        stdout.Write(".\n");
        for (var i = 1; i <= 160; i++)
            stdout.Write($"./entry{i:000}.txt\n");
        return 0;
    }

    // ---- cat ---------------------------------------------------------------
    // Copies stdin to stdout byte-for-byte (no decoding, no newline
    // translation) and exits 0. Exists so the stdin-forwarding smoke (#144)
    // can round-trip bytes through the real vtk binary without depending on
    // a Unix `cat` being on the verify host's PATH. `cat` is not a registry
    // key, so `vtk cat` takes the non-TTY passthrough path.
    private static int Cat()
    {
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        stdin.CopyTo(stdout);
        return 0;
    }
}

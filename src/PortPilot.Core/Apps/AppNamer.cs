using System.Text.RegularExpressions;
using PortPilot.Core.Model;

namespace PortPilot.Core.Apps;

/// <summary>
/// Turns command lines into names a person recognises: "Vite dev server", "Django", "OrderService
/// (Spring Boot)", "PostgreSQL". Ordered most specific first; the first match wins.
/// </summary>
public static partial class AppNamer
{
    private sealed record Rule(Regex Match, Func<Match, string> Name, string Kind);

    private static readonly Rule[] Rules =
    [
        new(Re(@"\bvite(\.js)?\b(?!.*\bbuild\b)"), _ => "Vite dev server", "Vite"),
        new(Re(@"\bnext(\.js)?\b.*\bdev\b|next-server|next/dist/server"), _ => "Next.js dev server", "Next.js"),
        new(Re(@"\bnext\b.*\bstart\b"), _ => "Next.js", "Next.js"),
        new(Re(@"\bnuxi?\b.*\bdev\b|nuxt/bin"), _ => "Nuxt dev server", "Nuxt"),
        new(Re(@"\bastro(\.js)?\b.*\bdev\b"), _ => "Astro dev server", "Astro"),
        new(Re(@"\bremix\b.*\bdev\b|@remix-run"), _ => "Remix dev server", "Remix"),
        new(Re(@"\bsvelte-kit\b|@sveltejs/kit"), _ => "SvelteKit dev server", "SvelteKit"),
        new(Re(@"\bng(\.js)?\b\s+serve|@angular/cli"), _ => "Angular dev server", "Angular"),
        new(Re(@"\bstorybook\b"), _ => "Storybook", "Storybook"),
        new(Re(@"webpack(-dev-server|\s+serve)"), _ => "webpack dev server", "webpack"),
        new(Re(@"react-scripts\b.*\bstart"), _ => "Create React App dev server", "React"),
        new(Re(@"\bgatsby\b.*\bdevelop"), _ => "Gatsby dev server", "Gatsby"),
        new(Re(@"\bdocusaurus\b"), _ => "Docusaurus", "Docusaurus"),
        new(Re(@"\bexpo\b.*\bstart|expo/bin"), _ => "Expo dev server", "Expo"),
        new(Re(@"\bwrangler\b.*\bdev"), _ => "Cloudflare Workers (wrangler)", "Wrangler"),
        new(Re(@"\bmanage\.py\s+runserver"), _ => "Django dev server", "Django"),
        new(Re(@"\bflask\b.*\brun\b|-m\s+flask"), _ => "Flask dev server", "Flask"),
        new(Re(@"\b(uvicorn|hypercorn|granian)\b\s+(?:[^-\s]\S*?\s+)*?([\w.]+):(\w+)"), m => $"{Module(m.Groups[2].Value)} ({Capital(m.Groups[1].Value)})", "ASGI"),
        new(Re(@"\bfastapi\s+(dev|run)\b"), _ => "FastAPI", "FastAPI"),
        new(Re(@"\bgunicorn\b\s+(?:[^-\s]\S*?\s+)*?([\w.]+):(\w+)"), m => $"{Module(m.Groups[1].Value)} (Gunicorn)", "WSGI"),
        new(Re(@"\bstreamlit\b"), _ => "Streamlit", "Streamlit"),
        new(Re(@"\bjupyter(-lab|-notebook|\s+(lab|notebook))"), _ => "Jupyter", "Jupyter"),
        new(Re(@"-m\s+http\.server|\bhttp-server\b|\blive-server\b|\bserve\s+-"), _ => "Static file server", "Static"),
        new(Re(@"\brails\b\s+s(erver)?\b|\bpuma\b"), _ => "Rails", "Rails"),
        new(Re(@"\bartisan\s+serve"), _ => "Laravel dev server", "Laravel"),
        new(Re(@"\bphp\b.*\s-S\s"), _ => "PHP built-in server", "PHP"),
        new(Re(@"\bhugo\b.*\bserver"), _ => "Hugo", "Hugo"),
        new(Re(@"\bjekyll\b.*\bserve"), _ => "Jekyll", "Jekyll"),
        new(Re(@"\bmkdocs\b.*\bserve"), _ => "MkDocs", "MkDocs"),
        // Java: a Spring Boot or plain jar, else the main class.
        new(Re(@"-jar\s+(?:\S*[/\\])?([\w.-]+?)(?:-\d[\w.-]*)?\.jar"), m => $"{m.Groups[1].Value} ({(Spring(m) ? "Spring Boot" : "Java")})", "Java"),
        new(Re(@"\bjava\b.*\s([a-z][\w]*\.)+([A-Z]\w*)(\s|$)"), m => $"{m.Groups[2].Value} ({(Spring(m) ? "Spring Boot" : "Java")})", "Java"),
        new(Re(@"\bdotnet\b\s+(?:exec\s+)?(?:\S*[/\\])?([\w.-]+)\.dll"), m => $"{m.Groups[1].Value} (.NET)", ".NET"),
        new(Re(@"\bdotnet\b\s+(watch|run)"), _ => ".NET app", ".NET"),
        new(Re(@"\bgo\s+run\b"), _ => "Go app (go run)", "Go"),
        new(Re(@"\bcargo\b.*\brun\b"), _ => "Rust app (cargo run)", "Rust"),
        new(Re(@"\bpostgres\b|postmaster"), _ => "PostgreSQL", "Database"),
        new(Re(@"\bmysqld\b|\bmariadbd\b"), _ => "MySQL", "Database"),
        new(Re(@"\bredis-server\b"), _ => "Redis", "Database"),
        new(Re(@"\bvalkey-server\b"), _ => "Valkey", "Database"),
        new(Re(@"\bmongod\b"), _ => "MongoDB", "Database"),
        new(Re(@"\belasticsearch\b|\bopensearch\b"), _ => "Elasticsearch", "Database"),
        new(Re(@"\brabbitmq\b|beam\.smp.*rabbit"), _ => "RabbitMQ", "Broker"),
        new(Re(@"\bkafka\b"), _ => "Kafka", "Broker"),
        new(Re(@"\bdocker-proxy\b.*-container-port\s+(\d+)"), m => $"Docker container (port {m.Groups[1].Value})", "Docker"),
        new(Re(@"\bcom\.docker|\bdockerd\b|\bvpnkit\b|\bgvproxy\b"), _ => "Docker", "Docker"),
        new(Re(@"\bollama\b"), _ => "Ollama", "AI"),
        new(Re(@"\bnginx\b"), _ => "nginx", "Web server"),
        new(Re(@"\bcaddy\b"), _ => "Caddy", "Web server"),
    ];

    /// <summary>The name and framework for an app, from its listening process and the process that started it.</summary>
    public static (string Name, string? Kind) Name(ProcessDetails primary, ProcessDetails? root, KnownPort? known)
    {
        foreach (var command in new[] { primary.CommandLine, root?.CommandLine })
        {
            if (string.IsNullOrEmpty(command))
            {
                continue;
            }

            foreach (var rule in Rules)
            {
                var match = rule.Match.Match(command);
                if (match.Success)
                {
                    return (rule.Name(match), rule.Kind);
                }
            }
        }

        // `npm run dev` in a project: name it after the script.
        if (root?.CommandLine is { } rootCommand && ScriptRun().Match(rootCommand) is { Success: true } script)
        {
            return ($"{script.Groups[1].Value} run {script.Groups[2].Value}", null);
        }

        // `node server.mjs`, `python app.py`, `bun index.ts`: the script is the name.
        if (primary.CommandLine is { } cmd && ScriptFile().Match(cmd) is { Success: true } file)
        {
            var runtime = file.Groups[1].Value.ToLowerInvariant() switch
            {
                var r when r.StartsWith("python", StringComparison.Ordinal) => "Python",
                "bun" => "Bun",
                "deno" => "Deno",
                "ruby" => "Ruby",
                "php" => "PHP",
                _ => "Node.js",
            };
            return ($"{file.Groups[2].Value} ({runtime})", runtime);
        }

        if (known is not null && known.Kind != KnownPortKind.DevServer)
        {
            return (known.Name, known.Kind == KnownPortKind.DataStore ? "Database" : null);
        }

        return (primary.DisplayName, null);
    }

    private static Regex Re(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [GeneratedRegex(@"\b(npm|pnpm|yarn|bun)\b\s+(?:run\s+)?([\w:-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptRun();

    [GeneratedRegex(@"(?:^|[/\\\s])(node|nodejs|bun|deno|tsx|ts-node|python[0-9.]*|ruby|php)(?:\.exe)?\s+(?:run\s+)?(?:-\S+\s+)*(?:\S*[/\\])?([\w.-]+\.(?:m?js|cjs|ts|mts|py|rb|php))\b", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptFile();

    private static bool Spring(Match m) => m.Value.Contains("spring", StringComparison.OrdinalIgnoreCase) || m.Value.Contains("boot", StringComparison.OrdinalIgnoreCase);

    private static string Module(string module) => module.Split('.')[^1] is var last && last is "main" or "app" or "server" or "asgi" or "wsgi" && module.Contains('.') ? module.Split('.')[^2] : module.Split('.')[^1];

    private static string Capital(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];
}

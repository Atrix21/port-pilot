using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PortPilot.Core.Apps;
using PortPilot.Core.Discovery;
using PortPilot.Core.Model;
using PortPilot.Core.Processes;

namespace PortPilot.Core.Tests;

[TestClass]
public sealed class AppNamerTests
{
    private static string Name(string command, string? root = null, string image = "node") =>
        AppNamer.Name(new ProcessDetails { ProcessId = 1, ImageName = image, CommandLine = command }, root is null ? null : new ProcessDetails { ProcessId = 2, ImageName = "npm", CommandLine = root }, null).Name;

    [TestMethod]
    [DataRow("node /app/node_modules/vite/bin/vite.js --port 5173", "Vite dev server")]
    [DataRow("node /app/node_modules/.bin/next dev --turbo", "Next.js dev server")]
    [DataRow("python3 manage.py runserver 0.0.0.0:8000", "Django dev server")]
    [DataRow("/venv/bin/python -m uvicorn shop.api.main:app --reload", "api (Uvicorn)")]
    [DataRow("java -jar build/libs/order-service-0.0.1-SNAPSHOT.jar --spring.profiles.active=dev", "order-service (Java)")]
    [DataRow("java -cp lib/* com.acme.billing.BillingApplication", "BillingApplication (Java)")]
    [DataRow("dotnet bin/Debug/net10.0/Atlas.Api.dll", "Atlas.Api (.NET)")]
    [DataRow("postgres -D /var/lib/postgresql/data", "PostgreSQL")]
    [DataRow("python3 -m http.server 8000", "Static file server")]
    [DataRow("node server.mjs 5173", "server.mjs (Node.js)")]
    public void Names_apps_the_way_people_would(string command, string expected) => Assert.AreEqual(expected, Name(command));

    [TestMethod]
    public void Falls_back_to_the_npm_script_that_started_it() =>
        Assert.AreEqual("npm run dev", Name("node ./dist/index", root: "npm run dev"));
}

[TestClass]
[OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
public sealed class AppAnalyzerTests
{
    [TestMethod]
    public void A_server_started_through_a_script_is_one_app_with_its_whole_process_family()
    {
        // npm-style: a tool (python here, npm in real life) runs a script through `sh`, which starts the
        // listener and a worker next to it.
        var port = FreePort();
        var script = Path.GetTempFileName();
        // A bare listener: http.server resolves its own host name before binding, which can hang on CI runners.
        File.WriteAllText(script, $"python3 -c \"import socket,time; s=socket.socket(); s.bind(('127.0.0.1', {port})); s.listen(); time.sleep(30)\" &\nsleep 30 &\nwait\n");
        using var launcher = Process.Start(new ProcessStartInfo("python3", ["-c", $"import subprocess; subprocess.run(['sh', '{script}'])"]) { UseShellExecute = false })!;
        try
        {
            var scanner = PlatformServices.CreateScanner();
            scanner.IncludeApps = true;
            AppInfo? app = null;
            for (var i = 0; i < 60 && app is null; i++)
            {
                Thread.Sleep(100);
                app = scanner.Scan(false).Apps?.FirstOrDefault(a => a.Endpoints.Any(e => e.Port == port));
            }

            Assert.IsNotNull(app, "the server is found as an app");
            // The root is whatever started the group (here the test host, as `turbo dev` would be for several servers);
            // what matters is that the shell npm-style tools use doesn't split the app.
            var family = ProcessTree.Descendants(launcher.Id).Append(launcher.Id).ToHashSet();
            Assert.IsTrue(family.IsSubsetOf(app.ProcessIds), $"launcher, sh, server and worker are all in the app ({app.ProcessIds.Count} processes)");
            Assert.IsTrue(family.Count >= 4, $"the test tree is what it should be ({family.Count})");
            Assert.IsTrue(app.Usage.MemoryBytes > 1024 * 1024, "memory is summed over the family");
        }
        finally
        {
            foreach (var pid in ProcessTree.Descendants(launcher.Id))
            {
                try { Process.GetProcessById(pid).Kill(); } catch (ArgumentException) { }
            }

            launcher.Kill();
            File.Delete(script);
        }
    }

    [TestMethod]
    public void Measures_this_process()
    {
        var sampler = new ResourceSampler();
        sampler.Sample([Environment.ProcessId]);
        var spin = Stopwatch.StartNew();
        while (spin.ElapsedMilliseconds < 200)
        {
        }

        var usage = sampler.Sample([Environment.ProcessId])[Environment.ProcessId];
        Assert.IsTrue(usage.CpuPercent > 20, $"a busy loop shows up as CPU ({usage.CpuPercent}%)");
        Assert.IsTrue(usage.MemoryBytes > 10 * 1024 * 1024, $"resident memory ({usage.MemoryBytes})");
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}

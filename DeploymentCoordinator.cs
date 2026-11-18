using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal sealed class DeploymentCoordinator {
    private readonly string queueDir;
    private readonly string contentRoot;
    internal DeploymentCoordinator(string dataDirectory, string contentRoot) {
        queueDir = Path.Combine(dataDirectory, "deployment-queue");
        this.contentRoot = contentRoot;
        Directory.CreateDirectory(queueDir);
    }
    private string FileAt(string name) => Path.Combine(queueDir, name);
    private bool WorkerAlive() {
        if (!File.Exists(FileAt("enabled.flag"))) return false;
        try {
            var heartbeat = JsonNode.Parse(File.ReadAllText(FileAt("heartbeat.json")));
            var timestamp = heartbeat?["updatedAtUtc"]?.ToString();
            return DateTimeOffset.TryParse(timestamp, out var updated) &&
                updated <= DateTimeOffset.UtcNow.AddMinutes(1) &&
                updated > DateTimeOffset.UtcNow.AddMinutes(-4);
        } catch (Exception) { return false; }
    }
    internal object Status() {
        JsonNode? last = null;
        var statusPath = FileAt("status.json");
        if (File.Exists(statusPath)) {
            try {
                var raw = File.ReadAllText(statusPath);
                if (raw.Length < 10000) last = JsonNode.Parse(raw);
            } catch (Exception) { /* Task may be atomically replacing status. */ }
        }
        var active = Path.GetFileName(contentRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var commit = active.Length == 40 && active.All(Uri.IsHexDigit) ? active : "local";
        return new {
            installed = File.Exists(FileAt("enabled.flag")),
            enabled = WorkerAlive(),
            workerMessage = !File.Exists(FileAt("enabled.flag")) ? "Worker not installed. Run scripts/Install-DeploymentTask.ps1 elevated." :
                WorkerAlive() ? "Worker is responding." : "Worker is installed but not responding. Check Scheduled Task AssetsWeb-Production-Updates.",
            currentVersion = commit,
            busy = File.Exists(FileAt("pending.json")) || File.Exists(FileAt("processing.json")),
            last
        };
    }
    internal (bool Accepted, string Message, string? RequestId) Enqueue(string action, string actor) {
        if (action is not ("update" or "rollback")) return (false, "Invalid deployment action", null);
        if (!WorkerAlive()) return (false, "Deployment worker is missing or inactive; check scheduled task AssetsWeb-Production-Updates.", null);
        if (File.Exists(FileAt("pending.json")) || File.Exists(FileAt("processing.json")))
            return (false, "Another deployment is already queued or running", null);
        var requestId = Guid.NewGuid().ToString("D");
        var payload = JsonSerializer.Serialize(new {
            schema = 1, id = requestId, action, actor,
            requestedAtUtc = DateTimeOffset.UtcNow.ToString("O")
        });
        try {
            using var file = new FileStream(FileAt("pending.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(file, new UTF8Encoding(false));
            writer.Write(payload);
            writer.Flush();
            file.Flush(true);
            return (true, "Queued for the protected IIS worker", requestId);
        } catch (IOException) {
            return (false, "Another deployment is already queued", null);
        }
    }
}

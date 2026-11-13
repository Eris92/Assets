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
            enabled = File.Exists(FileAt("enabled.flag")),
            currentVersion = commit,
            busy = File.Exists(FileAt("pending.json")) || File.Exists(FileAt("processing.json")),
            last
        };
    }
    internal (bool Accepted, string Message, string? RequestId) Enqueue(string action, string actor) {
        if (action is not ("update" or "rollback")) return (false, "Invalid deployment action", null);
        if (!File.Exists(FileAt("enabled.flag"))) return (false, "Deployment worker is not installed", null);
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

using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

internal sealed record EvidenceFile(string Sha256,string FileName,long Size,string UploadedAt,string UploadedBy);

internal sealed class EvidenceVault {
    private const long MaxSize = 10*1024*1024;
    private readonly string root;
    internal EvidenceVault(string dataDir) {
        root=Path.Combine(dataDir,"evidence");
        Directory.CreateDirectory(root);
    }
    internal bool Exists(string? hash) =>
        hash is { Length:64 } && hash.All(Uri.IsHexDigit) &&
        File.Exists(Path.Combine(root,hash.ToLowerInvariant()+".pdf"));

    internal async Task<EvidenceFile> Save(IFormFile file,string actor,CancellationToken ct) {
        if(file.Length is < 5 or > MaxSize || !string.Equals(Path.GetExtension(file.FileName),".pdf",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only PDF documents up to 10 MB are supported");
        var tmp=Path.Combine(root,"upload-"+Guid.NewGuid().ToString("N")+".tmp");
        var buffer=ArrayPool<byte>.Shared.Rent(65536);
        try {
            using var digest=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long written=0;
            await using(var source=file.OpenReadStream())
            await using(var target=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None,65536,
                FileOptions.Asynchronous|FileOptions.WriteThrough)) {
                int read;
                var pdfHeader=new byte[5];var seen=0;
                while((read=await source.ReadAsync(buffer.AsMemory(0,buffer.Length),ct))>0) {
                    written+=read;
                    if(written>MaxSize)throw new ArgumentException("PDF exceeds the maximum allowed size");
                    if(seen<5) {
                        var length=Math.Min(read,5-seen);
                        buffer.AsSpan(0,length).CopyTo(pdfHeader.AsSpan(seen));
                        seen+=length;
                    }
                    digest.AppendData(buffer.AsSpan(0,read));
                    await target.WriteAsync(buffer.AsMemory(0,read),ct);
                }
                await target.FlushAsync(ct);
                if(seen!=5 || !pdfHeader.AsSpan().SequenceEqual("%PDF-"u8))
                    throw new ArgumentException("File does not have a PDF signature");
            }
            var hash=Convert.ToHexString(digest.GetHashAndReset()).ToLowerInvariant();
            var destination=Path.Combine(root,hash+".pdf");
            try { File.Move(tmp,destination,false); }
            catch(IOException) when(File.Exists(destination)) {File.Delete(tmp);}
            // Metadata is advisory. Hash is derived from the bytes, never from user input.
            var safeFileName=Path.GetFileName(file.FileName);
            if(safeFileName.Length>180)safeFileName=safeFileName[..180];
            var evidence=new EvidenceFile(hash,safeFileName,written,DateTimeOffset.UtcNow.ToString("O"),actor);
            var meta=Path.Combine(root,hash+".json");
            if(!File.Exists(meta)) {
                var json=JsonSerializer.Serialize(evidence);
                try{await File.WriteAllTextAsync(meta,json,ct);}catch(IOException){/* PDF is already safely archived */ }
            }
            return evidence;
        } finally {
            ArrayPool<byte>.Shared.Return(buffer,clearArray:true);
            if(File.Exists(tmp))File.Delete(tmp);
        }
    }
    internal string GetPath(string hash) {
        if(!Exists(hash))throw new FileNotFoundException("Archived PDF was not found");
        return Path.Combine(root,hash.ToLowerInvariant()+".pdf");
    }
}

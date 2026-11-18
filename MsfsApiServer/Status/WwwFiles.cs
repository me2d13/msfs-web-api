namespace MsfsApiServer.Status
{
    /// <summary>
    /// Rules for the pages a user drops into the "www" directory next to the exe.
    /// Only the top level is served, and only file types a browser page can need.
    /// </summary>
    public static class WwwFiles
    {
        private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".html", ".htm", ".css", ".js", ".mjs", ".json", ".txt", ".map",
            ".svg", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".webp", ".woff", ".woff2",
        };

        private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            [".html"] = "text/html; charset=utf-8",
            [".htm"] = "text/html; charset=utf-8",
            [".css"] = "text/css; charset=utf-8",
            [".js"] = "text/javascript; charset=utf-8",
            [".mjs"] = "text/javascript; charset=utf-8",
            [".json"] = "application/json; charset=utf-8",
            [".txt"] = "text/plain; charset=utf-8",
            [".map"] = "application/json; charset=utf-8",
            [".svg"] = "image/svg+xml",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".ico"] = "image/x-icon",
            [".webp"] = "image/webp",
            [".woff"] = "font/woff",
            [".woff2"] = "font/woff2",
        };

        /// <summary>Pages in the www directory, for the "Local files" list.</summary>
        public static List<WwwFileInfo> List(string wwwDir)
        {
            var files = new List<WwwFileInfo>();
            try
            {
                if (!Directory.Exists(wwwDir)) return files;

                foreach (var path in Directory.EnumerateFiles(wwwDir))
                {
                    var extension = Path.GetExtension(path);
                    if (!extension.Equals(".html", StringComparison.OrdinalIgnoreCase) &&
                        !extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var info = new FileInfo(path);
                    files.Add(new WwwFileInfo
                    {
                        Name = info.Name,
                        Title = Path.GetFileNameWithoutExtension(info.Name),
                        Url = "/" + Uri.EscapeDataString(info.Name),
                        SizeBytes = info.Length,
                        ModifiedUtc = info.LastWriteTimeUtc,
                    });
                }
            }
            catch
            {
                // A directory we cannot read simply shows no files.
            }

            return files
                .OrderBy(f => f.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Maps a request for "/name" to a file inside wwwDir, or null when the name is not served.
        /// </summary>
        public static string? Resolve(string wwwDir, string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            // Routing already rejects slashes; keep the check so the rule holds wherever this is called from.
            if (name.Contains('/') || name.Contains('\\') || name.Contains("..")) return null;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;

            var extension = Path.GetExtension(name);
            if (string.IsNullOrEmpty(extension) || !AllowedExtensions.Contains(extension)) return null;

            try
            {
                var root = Path.GetFullPath(wwwDir);
                var full = Path.GetFullPath(Path.Combine(root, name));
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
                return File.Exists(full) ? full : null;
            }
            catch
            {
                return null;
            }
        }

        public static string ContentTypeFor(string fileName)
        {
            return ContentTypes.TryGetValue(Path.GetExtension(fileName), out var contentType)
                ? contentType
                : "application/octet-stream";
        }
    }
}

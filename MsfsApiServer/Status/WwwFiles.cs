namespace MsfsApiServer.Status
{
    /// <summary>
    /// Rules for the pages and assets a user drops into the "www" directory next to the exe.
    /// Subdirectories are served too (<c>/img/plane.jpg</c>), and only the file types listed
    /// below - the directory is not a general file share.
    /// </summary>
    public static class WwwFiles
    {
        /// <summary>
        /// The file types that are served, with their content type. Add to this table to serve
        /// something else; anything missing is refused with a 404.
        /// </summary>
        private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            // Pages and scripts
            [".html"] = "text/html; charset=utf-8",
            [".htm"] = "text/html; charset=utf-8",
            [".css"] = "text/css; charset=utf-8",
            [".js"] = "text/javascript; charset=utf-8",
            [".mjs"] = "text/javascript; charset=utf-8",
            [".map"] = "application/json; charset=utf-8",
            [".json"] = "application/json; charset=utf-8",
            [".txt"] = "text/plain; charset=utf-8",
            [".csv"] = "text/csv; charset=utf-8",
            [".xml"] = "application/xml; charset=utf-8",
            [".webmanifest"] = "application/manifest+json; charset=utf-8",
            [".wasm"] = "application/wasm",

            // Images
            [".svg"] = "image/svg+xml",
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".ico"] = "image/x-icon",
            [".webp"] = "image/webp",
            [".avif"] = "image/avif",
            [".bmp"] = "image/bmp",

            // Fonts
            [".woff"] = "font/woff",
            [".woff2"] = "font/woff2",
            [".ttf"] = "font/ttf",
            [".otf"] = "font/otf",

            // Media and documents
            [".mp3"] = "audio/mpeg",
            [".wav"] = "audio/wav",
            [".ogg"] = "audio/ogg",
            [".mp4"] = "video/mp4",
            [".webm"] = "video/webm",
            [".pdf"] = "application/pdf",
        };

        /// <summary>Pages in the top level of the www directory, for the "Local files" list.</summary>
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
        /// Maps a request path such as "views.html" or "img/plane.jpg" to a file inside wwwDir,
        /// or null when it is not served. Only the types in <see cref="ContentTypes"/> are served,
        /// and the result has to stay inside wwwDir.
        /// </summary>
        public static string? Resolve(string wwwDir, string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            if (path.Contains('\\') || path.Contains('\0')) return null;

            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) return null;

            foreach (var segment in segments)
            {
                if (segment == "." || segment == "..") return null;
                if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
            }

            var extension = Path.GetExtension(segments[^1]);
            if (string.IsNullOrEmpty(extension) || !ContentTypes.ContainsKey(extension)) return null;

            try
            {
                var root = Path.GetFullPath(wwwDir);
                var full = Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
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

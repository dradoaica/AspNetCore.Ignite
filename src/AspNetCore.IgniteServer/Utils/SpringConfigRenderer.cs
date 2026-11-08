using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AspNetCore.IgniteServer.Utils;

internal static class SpringConfigRenderer
{
    /// <summary>
    ///     Replaces each placeholder in <paramref name="template" /> with its XML-escaped value, and each of
    ///     <paramref name="xmlPlaceholders" /> with its value as is (XML elements). One pass: the values and fragments
    ///     inserted are not searched for placeholders (a fragment may contain e.g. <c>${SSL_KEY_STORE_PASSWORD}</c>).
    /// </summary>
    internal static string Render(
        string template,
        IReadOnlyDictionary<string, string?> placeholders,
        IReadOnlyDictionary<string, string?>? xmlPlaceholders = null
    )
    {
        xmlPlaceholders ??= new Dictionary<string, string?>();
        var names = placeholders.Keys.Concat(xmlPlaceholders.Keys).ToList();
        if (names.Count == 0) return template;

        // the longest name first, should one contain another
        var pattern = string.Join('|', names.OrderByDescending(name => name.Length).Select(Regex.Escape));
        return Regex.Replace(
            template,
            pattern,
            match => xmlPlaceholders.TryGetValue(match.Value, out var xml)
                ? xml ?? string.Empty
                : SecurityElement.Escape(placeholders[match.Value] ?? string.Empty)
        );
    }

    /// <summary>
    ///     Renders the template into a private file under the temp directory and returns its path. The template itself is
    ///     never modified, so every start re-applies the current configuration (and works on a read-only file system).
    /// </summary>
    internal static async Task<string> RenderToFileAsync(
        string templatePath,
        IReadOnlyDictionary<string, string?> placeholders,
        IReadOnlyDictionary<string, string?>? xmlPlaceholders = null,
        string? outputDirectory = null
    )
    {
        var template = await File.ReadAllTextAsync(templatePath, Encoding.UTF8).ConfigureAwait(false);
        outputDirectory ??= Path.Combine(Path.GetTempPath(), "aspnetcore-ignite-server");
        var outputPath = Path.Combine(outputDirectory, Path.GetFileName(templatePath));
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(outputDirectory);
        }
        else
        {
            // the rendered file may contain keystore passwords
            Directory.CreateDirectory(outputDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (File.Exists(outputPath)) File.Delete(outputPath);
        }

        FileStreamOptions options = new()
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        await using StreamWriter writer = new(outputPath, Encoding.UTF8, options);
        await writer.WriteAsync(Render(template, placeholders, xmlPlaceholders)).ConfigureAwait(false);

        return outputPath;
    }
}
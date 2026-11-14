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
    ///     The directory of this process in the temp directory, so that several servers on one machine do not share it.
    /// </summary>
    internal static string DefaultOutputDirectory =>
        Path.Combine(Path.GetTempPath(), $"aspnetcore-ignite-server-{Environment.ProcessId}");

    /// <summary>
    ///     Replaces each <c>{{NAME}}</c> of <paramref name="placeholders" /> in <paramref name="template" /> with its
    ///     XML-escaped value, and each of <paramref name="xmlPlaceholders" /> (as is, e.g., an XML comment) with its
    ///     value as is (XML elements). One pass: the values and fragments inserted are not searched for placeholders (a
    ///     fragment may contain e.g. <c>${SSL_KEY_STORE_PASSWORD}</c>).
    /// </summary>
    internal static string Render(
        string template,
        IReadOnlyDictionary<string, string?> placeholders,
        IReadOnlyDictionary<string, string?>? xmlPlaceholders = null
    )
    {
        Dictionary<string, string> replacements = new(StringComparer.Ordinal);
        foreach (var (name, value) in placeholders)
        {
            replacements[$"{{{{{name}}}}}"] = SecurityElement.Escape(value ?? string.Empty);
        }

        foreach (var (token, xml) in xmlPlaceholders ?? new Dictionary<string, string?>())
        {
            replacements[token] = xml ?? string.Empty;
        }

        if (replacements.Count == 0)
        {
            return template;
        }

        // the longest token first, should one contain another
        var pattern = string.Join('|', replacements.Keys.OrderByDescending(token => token.Length).Select(Regex.Escape));
        return Regex.Replace(template, pattern, match => replacements[match.Value]);
    }

    /// <summary>
    ///     Renders the template into a private file (by default under <see cref="DefaultOutputDirectory" />) and
    ///     returns its path. The template itself is never modified, so every start re-applies the current configuration
    ///     (and works on a read-only file system).
    /// </summary>
    internal static async Task<string> RenderToFileAsync(
        string templatePath,
        IReadOnlyDictionary<string, string?> placeholders,
        IReadOnlyDictionary<string, string?>? xmlPlaceholders = null,
        string? outputDirectory = null
    )
    {
        var template = await File.ReadAllTextAsync(templatePath, Encoding.UTF8).ConfigureAwait(false);
        outputDirectory ??= DefaultOutputDirectory;
        var outputPath = Path.Combine(outputDirectory, Path.GetFileName(templatePath));
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(outputDirectory);
        }
        else
        {
            // The rendered file may contain keystore passwords. A directory left by an earlier process of the same id
            // is made private again; one of another user cannot be (this throws), so it is not written to.
            const UnixFileMode privateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite |
                                                  UnixFileMode.UserExecute;
            Directory.CreateDirectory(outputDirectory, privateDirectory);
            File.SetUnixFileMode(outputDirectory, privateDirectory);
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }
        }

        FileStreamOptions options = new()
        {
            // a new file (not one planted in its place in the meantime) where the previous one was deleted
            Mode = OperatingSystem.IsWindows() ? FileMode.Create : FileMode.CreateNew,
            Access = FileAccess.Write,
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        await using StreamWriter writer = new(outputPath, Encoding.UTF8, options);
        await writer.WriteAsync(Render(template, placeholders, xmlPlaceholders)).ConfigureAwait(false);

        return outputPath;
    }

    /// <summary>Removes a rendered file, and its directory once empty; best effort.</summary>
    internal static void DeleteRendered(string renderedPath)
    {
        try
        {
            File.Delete(renderedPath);
            if (Path.GetDirectoryName(renderedPath) is { } directory &&
                !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The temp directory is cleaned up with the container, or by the system.
        }
    }
}

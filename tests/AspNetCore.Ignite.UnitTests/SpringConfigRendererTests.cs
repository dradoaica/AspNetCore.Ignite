using AspNetCore.IgniteServer.Utils;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

public sealed class SpringConfigRendererTests
{
    [Fact]
    public void Render_ReplacesAndEscapesPlaceholders()
    {
        var rendered = SpringConfigRenderer.Render(
            "<a ns=\"K8S_NAMESPACE\" pwd=\"SSL_KEY_STORE_PASSWORD\" svc=\"K8S_SERVICE_NAME\"/>",
            new Dictionary<string, string?>
            {
                ["K8S_NAMESPACE"] = "prod",
                ["SSL_KEY_STORE_PASSWORD"] = "a&b<\"c\">",
                ["K8S_SERVICE_NAME"] = null
            }
        );

        Assert.Equal("<a ns=\"prod\" pwd=\"a&amp;b&lt;&quot;c&quot;&gt;\" svc=\"\"/>", rendered);
    }

    [Fact]
    public void Render_InsertsXmlPlaceholdersVerbatim()
    {
        Assert.Equal(
            "<bean><property name=\"x\"><null/></property></bean>",
            SpringConfigRenderer.Render(
                "<bean><!-- X --></bean>",
                new Dictionary<string, string?>(),
                new Dictionary<string, string?> { ["<!-- X -->"] = "<property name=\"x\"><null/></property>" }
            )
        );
    }

    [Fact]
    public async Task RenderToFileAsync_LeavesTemplateUntouched()
    {
        var directory = Path.Combine(Path.GetTempPath(), "spring-config-renderer-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var templatePath = Path.Combine(directory, "spring-config.xml");
            const string template = "<p value=\"K8S_NAMESPACE\"/>";
            await File.WriteAllTextAsync(templatePath, template);
            var outputDirectory = Path.Combine(directory, "out");

            var first = await SpringConfigRenderer.RenderToFileAsync(
                templatePath,
                new Dictionary<string, string?> { ["K8S_NAMESPACE"] = "first" },
                outputDirectory: outputDirectory
            );
            var second = await SpringConfigRenderer.RenderToFileAsync(
                templatePath,
                new Dictionary<string, string?> { ["K8S_NAMESPACE"] = "second" },
                outputDirectory: outputDirectory
            );

            Assert.Equal(template, await File.ReadAllTextAsync(templatePath));
            Assert.Equal(first, second);
            Assert.Equal("<p value=\"second\"/>", await File.ReadAllTextAsync(second));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(second));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void Render_DoesNotSearchInsertedValuesAndFragments()
    {
        var rendered = SpringConfigRenderer.Render(
            "<a ns=\"K8S_NAMESPACE\" pwd=\"SSL_KEY_STORE_PASSWORD\"/><!-- X -->",
            new Dictionary<string, string?>
            {
                ["K8S_NAMESPACE"] = "SSL_KEY_STORE_PASSWORD",
                ["SSL_KEY_STORE_PASSWORD"] = "secret"
            },
            new Dictionary<string, string?>
                { ["<!-- X -->"] = "<b key=\"K8S_NAMESPACE\" value=\"${SSL_KEY_STORE_PASSWORD}\"/>" }
        );

        Assert.Equal(
            "<a ns=\"SSL_KEY_STORE_PASSWORD\" pwd=\"secret\"/><b key=\"K8S_NAMESPACE\" value=\"${SSL_KEY_STORE_PASSWORD}\"/>",
            rendered
        );
    }
}

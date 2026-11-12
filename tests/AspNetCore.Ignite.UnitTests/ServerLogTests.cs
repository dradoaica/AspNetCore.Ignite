using AspNetCore.Ignite.IntegrationTests;
using AspNetCore.IgniteServer;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Xunit;

namespace AspNetCore.Ignite.UnitTests;

public sealed class ServerLogTests
{
    public static TheoryData<string, string> Messages
    {
        get
        {
            TheoryData<string, string> data = [];
            foreach (var constant in typeof(ServerLog).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                data.Add(constant.Name, (string)constant.GetRawConstantValue()!);
            }

            return data;
        }
    }

    /// <summary>
    ///     The integration tests wait for these texts in the container logs, so the server must still log them.
    /// </summary>
    [Theory]
    [MemberData(nameof(Messages))]
    public void MessageTheIntegrationTestsWaitFor_IsInTheServerCode(string name, string text) => Assert.True(
        ServerStringLiterals.Value.Any(literal => literal.Contains(text, StringComparison.Ordinal)),
        $"ServerLog.{name} (\"{text}\") is not part of any string in the server: update it to the reworded message."
    );

    /// <summary>
    ///     Every string literal of the server assembly (the compiler joins concatenated constants into one).
    /// </summary>
    private static readonly Lazy<List<string>> ServerStringLiterals = new(() =>
        {
            using PEReader assembly = new(File.OpenRead(typeof(IgniteServerRunner).Assembly.Location));
            var metadata = assembly.GetMetadataReader();
            List<string> literals = [];
            var handle = MetadataTokens.UserStringHandle(1);
            while (!handle.IsNil)
            {
                literals.Add(metadata.GetUserString(handle));
                handle = metadata.GetNextHandle(handle);
            }

            return literals;
        }
    );
}

using System.Reflection;

namespace AspNetCore.Ignite.UnitTests;

/// <summary>
///     An implementation of an interface whose calls are answered by one function, given the member name and the
///     arguments. Name the members with <c>nameof</c> (e.g., <c>nameof(ICluster.IsActive)</c>), so that a renamed member
///     fails to compile; a property getter is passed as the property name.
/// </summary>
public class Fake : DispatchProxy
{
    private Func<string, object?[], object?> answer = (name, _) => throw new NotSupportedException(name);

    public static T Of<T>(Func<string, object?[], object?> answer)
        where T : class
    {
        var fake = Create<T, Fake>();
        ((Fake)(object)fake).answer = answer;
        return fake;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        var name = targetMethod!.Name;
        return answer(name.StartsWith("get_", StringComparison.Ordinal) ? name[4..] : name, args ?? []);
    }
}

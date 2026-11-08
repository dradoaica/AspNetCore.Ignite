using System.Reflection;

namespace AspNetCore.Ignite.UnitTests;

/// <summary>An implementation of an interface whose calls a function answers, by member name (properties as get_X).</summary>
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

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
        answer(targetMethod!.Name, args ?? []);
}

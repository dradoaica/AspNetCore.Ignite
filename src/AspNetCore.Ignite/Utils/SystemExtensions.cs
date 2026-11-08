using System;
using System.Text.RegularExpressions;

namespace AspNetCore.Ignite.Utils;

/// <summary>Type name helpers for cache configurations.</summary>
public static partial class SystemExtensions
{
    /// <summary>
    ///     Returns the full name of the specified type without any assembly version info. The reason why this method is
    ///     needed is that a generic type's FullName contains the full AssemblyQualifiedName of its item types.
    /// </summary>
    /// <param name="type">Type to get the full name for.</param>
    /// <returns>Full name of the specified type without additional info of the assembly.</returns>
    public static string FullNameWithoutAssemblyInfo(this Type type) => type.FullName is { } fullName
        ? AssemblyVersionInfo().Replace(fullName, string.Empty) : type.Name;

    // ", Version=..., Culture=..., PublicKeyToken=..." of every type argument, up to its closing bracket
    [GeneratedRegex(@", Version=[^\]]*")]
    private static partial Regex AssemblyVersionInfo();
}

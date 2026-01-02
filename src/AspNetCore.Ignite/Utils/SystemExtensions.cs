using System;

namespace AspNetCore.Ignite.Utils;

public static class SystemExtensions
{
    /// <summary>
    ///     Returns the full name of the specified type without any assembly version info. The reason why this method is
    ///     needed is that a generic type's FullName contains the full AssemblyQualifiedName of its item type.
    /// </summary>
    /// <param name="type">Type to get the full name for.</param>
    /// <returns>Full name of the specified type without additional info of the assembly.</returns>
    public static string FullNameWithoutAssemblyInfo(this Type type) =>
        !type.IsGenericType ? type.FullName : RemoveAssemblyInfo(type.FullName);

    /// <summary>Removes all assembly info from the specified type name.</summary>
    /// <param name="typeName">Type name to remove assembly info from.</param>
    /// <returns>Type the name without assembly info.</returns>
    private static string RemoveAssemblyInfo(string typeName)
    {
        // Get start of "Version=..., Culture=..., PublicKeyToken=..." string.
        var versionIndex = typeName.IndexOf("Version=", StringComparison.Ordinal);
        if (versionIndex >= 0)
        {
            // Get end of "Version=..., Culture=..., PublicKeyToken=..." string for generics.
            var endIndex = typeName.IndexOf(']', versionIndex);
            // Get end of "Version=..., Culture=..., PublicKeyToken=..." string for non-generics.
            endIndex = endIndex >= 0 ? endIndex : typeName.Length;
            // Remove version info.
            typeName = typeName.Remove(versionIndex - 2, endIndex - versionIndex + 2);
        }

        return typeName;
    }
}

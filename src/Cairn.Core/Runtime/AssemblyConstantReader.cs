using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Cairn.Core.Runtime;

/// <summary>
/// Reads compile-time string constants straight out of assembly metadata.
///
/// The game's version is only reliably available as the
/// Vintagestory.API.Config.GameVersion.ShortGameVersion constant. Assembly attributes
/// disagree with it — a 1.21.5 build reports AssemblyVersion 1.0.0.0 and FileVersion
/// 1.21.0 — so Cairn reads the constant the game itself uses.
///
/// Uses System.Reflection.Metadata from the shared framework: no NuGet dependency, and
/// nothing is loaded or executed, so this works against an assembly built for another
/// architecture.
/// </summary>
public static class AssemblyConstantReader
{
    public static string? ReadStringConstant(
        string assemblyPath, string typeNamespace, string typeName, string fieldName)
    {
        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);

            if (!pe.HasMetadata) return null;
            var md = pe.GetMetadataReader();

            foreach (var typeHandle in md.TypeDefinitions)
            {
                var type = md.GetTypeDefinition(typeHandle);
                if (!md.GetString(type.Name).Equals(typeName, StringComparison.Ordinal)) continue;
                if (!md.GetString(type.Namespace).Equals(typeNamespace, StringComparison.Ordinal)) continue;

                foreach (var fieldHandle in type.GetFields())
                {
                    var field = md.GetFieldDefinition(fieldHandle);
                    if (!md.GetString(field.Name).Equals(fieldName, StringComparison.Ordinal)) continue;

                    var constantHandle = field.GetDefaultValue();
                    if (constantHandle.IsNil) return null;

                    var constant = md.GetConstant(constantHandle);
                    if (constant.TypeCode != ConstantTypeCode.String) return null;

                    // A Constant blob is raw UTF-16 covering the whole blob — not a
                    // length-prefixed serialized string, which reads out of bounds.
                    // Decoding the bytes directly avoids depending on whether
                    // BlobReader.ReadUTF16 counts chars or bytes.
                    var blob = md.GetBlobReader(constant.Value);
                    var bytes = blob.ReadBytes(blob.RemainingBytes);
                    return System.Text.Encoding.Unicode.GetString(bytes);
                }
            }

            return null;
        }
        catch (Exception e) when (e is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

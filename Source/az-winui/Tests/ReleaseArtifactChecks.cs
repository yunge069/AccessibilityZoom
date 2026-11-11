using System.Reflection.PortableExecutable;

internal static class ReleaseArtifactChecks
{
    internal static void VerifySymbols(string directory)
    {
        string assembly = Path.Combine(directory, "AccessibilityZoom.dll");
        using var stream = File.OpenRead(assembly);
        using var reader = new PEReader(stream);
        if (reader.ReadDebugDirectory().Any(entry => entry.Type is
                DebugDirectoryEntryType.EmbeddedPortablePdb or DebugDirectoryEntryType.CodeView))
            throw new InvalidDataException("Release assembly contains debug symbols or a PDB reference that could expose local build paths.");

        if (Directory.EnumerateFiles(directory, "AccessibilityZoom*.pdb", SearchOption.AllDirectories).Any())
            throw new InvalidDataException("Release directory contains project PDB files.");

        Console.WriteLine("Release privacy check passed: no project debug symbols or PDB references.");
    }
}

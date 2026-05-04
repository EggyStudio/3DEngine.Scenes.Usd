using System.IO;
using System.IO.Compression;

namespace Engine;

/// <summary>
/// Translates USDZ-packaged texture asset paths returned by the USD asset resolver
/// (e.g. <c>/abs/foo.usdz[textures/bar.png]</c>) into a synthetic
/// <c>__embedded__/</c> path that <see cref="TextureAssetLoader"/> can decode through
/// the regular asset pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the embedded-image handling in <c>GltfModelReader</c>: bytes are extracted
/// from the .usdz zip container once, published into
/// <see cref="InMemoryAssetReader.ProcessWideStore"/>, and the synthetic path is
/// returned in place of the bracketed packaged-path so downstream code (matching the
/// path against <c>__embedded__/</c> in <c>SceneSpawner.ResolveTexturePath</c>) treats
/// it as already-rooted.
/// </para>
/// <para>
/// Non-packaged paths (regular filesystem paths, http URIs, paths that lack the
/// <c>[member]</c> suffix) are returned unchanged so existing behaviour is preserved.
/// </para>
/// </remarks>
/// <seealso cref="UsdMaterialReader"/>
/// <seealso cref="UsdSceneReader"/>
/// <seealso cref="InMemoryAssetReader"/>
internal static class UsdEmbeddedTextureResolver
{
    private static readonly ILogger Logger = Log.Category("Engine.Scenes.Usd.Embedded");

    /// <summary>
    /// Resolves a USD-side texture asset path. If <paramref name="assetPath"/> matches
    /// the packaged-asset syntax <c>archive.usdz[member]</c>, the member's bytes are
    /// extracted from the archive and published under a synthetic <c>__embedded__/usdz/</c>
    /// path which is returned. Otherwise the input is returned verbatim.
    /// </summary>
    /// <param name="assetPath">The asset path returned by the USD resolver.</param>
    /// <returns>Either the synthetic embedded path or <paramref name="assetPath"/>.</returns>
    public static string Resolve(string assetPath)
    {
        if (string.IsNullOrEmpty(assetPath)) return assetPath;

        // USD packaged-asset syntax: "<archive>[<member>]". The bracket pair must be at
        // the tail; brackets earlier in the path are part of the archive name.
        int ket = assetPath.Length - 1;
        if (assetPath[ket] != ']') return assetPath;
        int bra = assetPath.LastIndexOf('[', ket - 1);
        if (bra <= 0) return assetPath;

        var archive = assetPath.Substring(0, bra);
        var member = assetPath.Substring(bra + 1, ket - bra - 1);
        var synthetic = TryPublishFromArchive(archive, member);
        return synthetic ?? assetPath;
    }

    private static string? TryPublishFromArchive(string archive, string member)
    {
        try
        {
            if (!File.Exists(archive))
            {
                Logger.Debug($"UsdEmbeddedTextureResolver: archive '{archive}' not found on disk; leaving '{member}' unresolved.");
                return null;
            }

            using var zip = ZipFile.OpenRead(archive);
            var entry = zip.GetEntry(member) ?? FindByBaseName(zip, member);
            if (entry is null)
            {
                Logger.Debug($"UsdEmbeddedTextureResolver: member '{member}' not found in '{archive}'.");
                return null;
            }

            byte[] bytes;
            using (var s = entry.Open())
            using (var ms = new MemoryStream(checked((int)entry.Length)))
            {
                s.CopyTo(ms);
                bytes = ms.ToArray();
            }

            var archiveStem = Path.GetFileNameWithoutExtension(archive);
            var safeMember = member.Replace('\\', '/').TrimStart('/');
            var synthetic = $"__embedded__/usdz/{archiveStem}/{safeMember}";
            InMemoryAssetReader.Publish(new AssetPath(synthetic), bytes);
            Logger.Debug($"UsdEmbeddedTextureResolver: published '{archive}'[{member}] ({bytes.Length} bytes) as '{synthetic}'.");
            return synthetic;
        }
        catch (Exception ex)
        {
            Logger.Debug($"UsdEmbeddedTextureResolver: failed to extract '{archive}'[{member}]: {ex.Message}");
            return null;
        }
    }

    private static ZipArchiveEntry? FindByBaseName(ZipArchive zip, string member)
    {
        var basename = Path.GetFileName(member);
        if (string.IsNullOrEmpty(basename)) return null;
        foreach (var e in zip.Entries)
            if (string.Equals(Path.GetFileName(e.FullName), basename, StringComparison.Ordinal))
                return e;
        return null;
    }
}


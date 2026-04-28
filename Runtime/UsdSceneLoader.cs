namespace Engine;

/// <summary>
/// <see cref="IAssetLoader{T}"/> that loads USD files (<c>.usd / .usda / .usdc</c>) into a
/// <see cref="SceneAsset"/>. Delegates the actual stage parsing to <see cref="UsdSceneReader"/>
/// so command-line / editor consumers can reuse the same conversion logic.
/// </summary>
/// <remarks>
/// <para>
/// Registered with the <see cref="AssetServer"/> by <see cref="UsdScenesPlugin"/>. Once active
/// you can do:
/// <code>
/// Handle&lt;SceneAsset&gt; sceneHandle = server.Load&lt;SceneAsset&gt;("scenes/sponza.usda");
/// </code>
/// and the resulting <see cref="SceneAsset"/> will be published to <c>Assets&lt;SceneAsset&gt;</c>
/// after the background worker completes (with an <c>AssetEvent&lt;SceneAsset&gt;.Added</c>).
/// </para>
/// <para>
/// The reader walks the prim tree on the calling worker thread and produces a fully
/// populated <see cref="Scene"/> (meshes, cameras, UsdLux lights, UsdPreviewSurface
/// materials). See <see cref="UsdSceneReader"/> for the conversion contract and the list
/// of payload kinds covered.
/// </para>
/// </remarks>
/// <seealso cref="UsdScenesPlugin"/>
/// <seealso cref="UsdSceneReader"/>
/// <seealso cref="IAssetLoader{T}"/>
public sealed class UsdSceneLoader : IAssetLoader<SceneAsset>
{
    private readonly UsdSceneReader _reader = new();

    /// <inheritdoc />
    public string[] Extensions => [".usd", ".usda", ".usdc", ".usdz"];

    /// <inheritdoc />
    public async Task<AssetLoadResult<SceneAsset>> LoadAsync(AssetLoadContext context, CancellationToken ct)
    {
        try
        {
            var scene = await _reader.ReadAsync(context, SceneImportSettings.Default, ct);
            var asset = new SceneAsset
            {
                Scene = scene,
                SourcePath = context.Path.ToString(),
                SourceFormat = _reader.FormatId,
            };
            return AssetLoadResult<SceneAsset>.Ok(asset);
        }
        catch (Exception ex)
        {
            return AssetLoadResult<SceneAsset>.Fail($"USD scene load failed for '{context.Path}': {ex.Message}");
        }
    }
}


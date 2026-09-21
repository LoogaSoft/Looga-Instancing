# Looga Instancing

Shared mesh rendering and placement tools for Looga Terrain and Looga Graphics Pro.
The runtime does not depend on either product, FR2, BRG Instanced Renderer, or Flora.

## Supported target

Unity 6000.3, URP 17.3, Windows and Direct3D 12. Use Forward+ or Deferred+ for the complete clustered lighting path.
Keep the SRP Batcher enabled. Retain BatchRendererGroup shader variants in player builds.

The renderer accepts opaque and cutout triangle meshes with standard DOTS instancing variants.
It checks required passes and reports unsupported sources before taking scene rendering ownership.
Compatible shaders need no Looga-specific rendering rewrite.
Custom data, deformation, texture residency and auxiliary passes still require their corresponding shader contracts.
Transparent sorting and SkinnedMeshRenderer submission are not supported by this mesh renderer. Scene providers retain their native rendering.

## Ownership

- `InstanceRenderer`: BRG registration, GPU frustum and distance culling, LOD, buffer residency and changed-instance uploads.
- `InstanceContainer`: serialized placements, stable IDs, edits, streamed prototype leases and render ownership.
- `SceneInstanceProvider`: reversible submission of existing scene mesh hierarchies. Gameplay components stay on their original objects.
- `MeshScatterAuthoring`: deterministic mesh scattering, painting, source masks and localized regeneration.
- `InstanceColliderResidency`: nearby collision proxies independent of visual visibility.
- `VegetationInteractor`, wind profiles and page sources: shared vegetation interaction, deformation inputs and streaming contracts.
- `IInstanceSourceAdapter`: common diagnostics for mixed Looga/native ownership without forcing sources into a Looga data model.
- `RuntimeInstanceSourceAdapter`: transactional snapshots from application-owned runtime placement systems into a dedicated container.
- `InstancePrototypeRegistry`: deterministic structural IDs and shared CPU prototype metadata across renderer owners.
- `InstanceWorldCells`: content-specific occupancy, residency, dirty revisions, and diagnostics for later visibility and streaming passes.
- Optional SpeedTree assembly: native wind integration with supported Unity versions.
- Optional Splines assembly: public Unity spline geometry, exclusion corridors, edits, Undo and source replacement.

TerrainData tree/detail generation, terrain ground bindings, terrain RVT and terrain-tool adapters remain in Looga Terrain.
Graphics Pro retains lighting, general RVT, SVT and its shader-specific instance material profiles.

## Basic runtime use

```csharp
using LoogaSoft.Instancing;

var instances = new InstanceRenderer { MaxDistance = 1000 };
int prototype = instances.Register(InstancePrototype.FromPrefab(prefab));
InstanceHandle handle = instances.Add(prototype, transformMatrix);
instances.Flush();
// Update and remove handles through this owner. Dispose the owner when its scene unloads.
instances.Dispose();
```

## General prefab compiler

`InstancePrototypeCompiler.Compile` reads an ordinary prefab or scene hierarchy without changing it. The result contains the reusable `InstancePrototype`, a source revision, extracted renderer/mesh/material/LOD/bounds/shadow/collider/wind metadata, and structured diagnostics. Existing `InstancePrototype.FromPrefab` calls use this compiler and keep their exception-based compatibility behavior.

The session cache reuses derived prototype data when another unchanged hierarchy has the same render structure. A mesh, material, shader, profile, LOD, transform, renderer flag, or hierarchy change produces a new revision. Editor asset imports invalidate entries that reference the imported objects. Deleting an asset clears the bounded session cache because Unity no longer exposes the deleted object's instance ID.

`SkinnedMeshRenderer` remains an error because it needs a deformation-specific provider. Other renderer types are reported and stay native while compatible `MeshRenderer` parts compile. The compiler does not remove components, edit materials, create prefab variants, or require a separate converted asset.

Use `InstanceContainer` for authored populations. Assign its prefab, optional material profile and placement data in the Inspector.
Use **GameObject > Looga > Render Selected Roots With BRG** for reversible scene ownership.
Use **Tools > Looga > Instancing > Bake Selected Instance Snapshot** to review a placement snapshot before enabling it.
Standalone prototype export preserves native mesh, LOD and collider references. It rejects unresolved external scene and vendor dependencies.

## Source adapters and fallback

`SceneInstanceProvider`, `InstanceContainer`, Looga Terrain's `TerrainVegetationProvider`, and the optional MicroVerse bridges expose `IInstanceSourceAdapter.SourceStatus`. The status reports requested populations, Looga-owned populations, native fallbacks, update waits, and the current diagnostic. Mixed ownership is valid: one supported root can use BRG while another incompatible root remains on its ordinary renderer.

Scene and TerrainData adapters acquire visibility only after a complete prototype and placement build. An unsupported material, prototype, external owner, or incomplete tool update leaves native rendering active. Disabling the adapter releases only the visibility it acquired. It does not disable colliders, scripts, source GameObjects, shared materials, TerrainData, or authoring tools.

Runtime systems can implement `IRuntimeInstanceSource` and bind it through `RuntimeInstanceSourceAdapter`. A capture is atomic. A failed or incomplete revision keeps the last complete Looga population until the producer signals another change. The target `InstanceContainer` must be dedicated to that adapter; disabling the adapter clears the derived target while leaving the producer's data untouched.

## Material bindings

Editor ownership validation rejects known Unity shader compiler errors. Fix the reported errors before converting the source renderer. Keyword declarations alone do not establish that a shader variant compiles. Player builds still require variant inclusion tests; runtime inspection cannot recover stripped variants.

`InstanceMaterialProfile.CreateMaterial` can return an `InstanceMaterialBinding` for a registered draw part.
The binding owns its material and resource leases. The renderer unregisters the draw before disposing the binding.
A binding must retain the validated shader. The default implementation uses the original material.

Graphics Pro supplies `LoogaSvtInstanceProfile`. Assign its baked SVT asset and page capacity, then assign the profile to the population.
It leases materials from Graphics Pro's existing SVT registry. It does not allocate another virtual texture system.
The Graphics Pro SVT renderer feature collects feedback from the BRG draws and streams the requested pages.
Native SVT components use renderer property blocks. Scene providers require an explicit conversion to the instance profile before taking those sources.

## Upgrade from Looga Terrain's instance module

The general runtime namespace is now `LoogaSoft.Instancing`. General editor tools use `LoogaSoft.Instancing.Editor`.
SpeedTree profiles use `LoogaSoft.Instancing.SpeedTree`. TerrainData adapters retain `LoogaSoft.Terrain.Instances`.
Update source imports and assembly references for the moved APIs.
Existing Unity script GUIDs are retained. Moved serialized types declare their former namespace and assembly through `MovedFrom`.
Placement file formats and stable IDs are unchanged.

Both Looga products declare this shared package as a dependency. Local consumers must resolve all three package paths through Package Manager.
The shared package must not depend on the full Graphics Pro or Terrain product.

## Shared visibility

The URP visibility feature builds one prior-frame depth pyramid for each eligible
camera scope. Instances and Looga Terrain consume the same context. Impostor and
HLOD systems can register with `InstanceVisibility` without adding another depth
capture. Compatible overlay cameras share the base camera context. Game, Scene,
render-scope, reflection, and preview cameras keep separate contexts when needed.

The service skips duplicate captures when more than one renderer feature refers
to the same camera scope. Stereo and unsupported camera types fail visible. The
ordinary BRG, TerrainData, prefab, collider, and authoring fallbacks remain.

## World registry and cells

Every renderer acquires immutable draw prototypes through `InstancePrototypeRegistry`. Share-compatible meshes, materials, LODs, bounds, and draw settings receive the same stable structural ID. The registry verifies compatibility before sharing and isolates hash collisions. Material profiles remain source-specific unless the exact compiled prototype is reused because wind and auxiliary bindings may depend on their source object.

After source uploads complete, `InstanceRenderer` publishes aggregated occupancy to `InstanceWorldCells`. The registry stores counts per source, cell, and prototype rather than copying placement records. Trees, details, grass, scene objects, painted/runtime objects, terrain surfaces, impostors, and HLODs have independent default cell sizes. Callers can query residency and diagnostics, mark existing cells dirty by bounds, copy a bounded number of dirty records, and acknowledge an exact revision. Removed cells remain as dirty unloaded tombstones until acknowledged.

These registries are derived runtime caches. They do not replace prefabs, placement assets, TerrainData, MicroVerse output, colliders, or native fallback state. Pass 6 may merge compatible GPU submissions across source boundaries; this pass only canonicalizes CPU prototype metadata and spatial ownership.

## Interactive near-field proxies

Add `InteractiveInstanceResidency` beside an `InstanceContainer` when nearby
instances need normal GameObject gameplay. Assign the ordinary gameplay prefab
as the proxy prototype and add one or more `VegetationInterest` components to
players, AI agents, or interaction cameras. The residency owner promotes a
bounded number of nearby placements and pools released proxies.

The proxy keeps the placement's stable source and placement IDs through
`InstanceProxyIdentity`. The original renderer handle stays allocated but is
hidden until the proxy returns to the GPU representation. Proxy movement is
written back in one placement transaction when ownership returns. Destroying a
proxy removes the source placement.

Implement `IInstanceProxyStateAdapter` for save-owned state,
`IInstanceProxyLifecycle` for promotion and release events,
`IInstanceProxyNetworkBridge` for network integration, and
`IInstanceHarvestHandler` for harvest behavior. Bind an application-owned
`InstanceProxyStateStore` when state must survive residency component or
streamed-cell lifetimes. These interfaces do not require a specific save,
interaction, destruction, or network package.

Promotion is opt-in. An unconfigured container keeps its normal GPU or native
path. Use a positive release margin to prevent rapid ownership changes near the
interest boundary. Keep the maximum proxy count and per-sync promotion budget
small enough for the target gameplay frame.

## Relightable impostors

Open **Tools > Looga > Instancing > Bake Impostor** to create derived cross-card
or octahedral impostor assets from an ordinary prefab. Choose an output asset,
tile resolution, view layout, alpha threshold, and LOD transition distances.
The baker writes the atlas textures, generated mesh, material, and compiled
prefab beside the `InstanceImpostorAsset`. It does not edit the source prefab or
source materials.

The albedo-opacity, object-normal, and material-mask atlases support runtime
relighting. `Looga/Instancing/Relightable Impostor` includes URP forward,
shadow, depth, depth-normal, and motion-vector passes. It also includes DOTS
instancing, LOD cross-fade, and the shared vegetation wind properties. The
compiled prefab uses a standard `LODGroup`, so it can remain a normal Unity
prefab or pass through `InstancePrototypeCompiler`.

Cross-card mode costs fewer views and atlas pages. Octahedral mode preserves
more view-dependent shape. Bake both modes for representative trees and compare
silhouette quality, overdraw, memory, and transition distance in the target
scene. The baker is an offline authoring tool; bake time is not runtime cost.

## Validation and limits

Package tests cover serialized placements, hierarchy migration, source ownership, material leases, mesh authoring, gameplay and spline edits.
Consumer tests compare native and BRG output and exercise actual MicroVerse road and RAM generation.
Validation applies to the installed versions and the supported target. It does not establish performance for a mature forest or every custom shader.
Dense source workloads, visibility modes and a five-minute streaming-pressure soak have measured checks. The remaining release gates are listed below.

## Visibility and quality

Direct culling remains the default. It evaluates each mesh part without retaining a full selection buffer.
Shared culling evaluates a prototype once per view and reuses the result for its mesh parts.
Hierarchical culling first checks conservative groups of 64 slots. CachedHierarchy retains those group bounds until a source upload or storage replacement invalidates them. Nearby slot ordering gives tighter groups; moving instances remain correct but can make groups less useful.

These modes are choices, not a performance ranking. Compare them with the same scene, materials, camera, shadows and distances. A renderer with one cheap mesh part can be faster with Direct. Shared selection costs 16 bytes per reserved instance per view; cached parent bounds cost another 16 bytes per 64 slots.

Hierarchical modes maintain a Morton-ordered slot index. This keeps nearby instances in the same conservative group without changing stable instance handles. The order is rebuilt only after uploads settle and at most once per 30 play frames.

Multi-part prototypes without an active mesh-LOD crossfade use one two-dimensional culling dispatch for every part. Crossfading prototypes retain the separate-part path because their packed fade commands differ. This changes dispatch overhead only; material and submesh ownership remain unchanged.

Use InstanceRenderer.SetQuality for each prototype or InstanceContainer.ConfigureQuality for an authored container. TerrainVegetationProvider.ConfigureQuality sets separate tree/detail policies and the visibility mode for resident and streamed terrain sources. It rebuilds the provider; source TerrainData remains unchanged.

Density and MinimumPixels require an explicitly Decorative source without colliders. Density uses stable source keys. It does not remove gameplay records or collision proxies. Use SetVisibilityKey with a persistent source ID when calling the low-level API directly. Containers derive keys from their placement IDs.

ShadowDistance, MinimumShadowLod and ShadowSplits control shadow work independently of camera LOD. A zero distance or split limit inherits the existing renderer policy. ShadowSplits limits leading directional cascades; point and spot projections remain unchanged. ShadowFadeDistance uses stable stochastic whole-instance thinning, not per-pixel shader dithering.

## Optional URP depth occlusion

Add InstanceOcclusionRendererFeature to an explicitly selected URP renderer asset. It is not installed into scenes automatically. The optional Universal assembly requires URP 17.3. The qualified target is Windows/D3D12.

This feature captures opaque depth after opaque rendering and builds a conservative farthest-depth pyramid for the next frame. The following camera cull consumes that history during its first visibility pass. It does not request a depth or depth-normal prepass and does not run a second instance-culling pass. It retains depth and shadow submissions. Transparent water does not become an occluder.

Adaptive capture is enabled by default. A camera enables Hi-Z after a sampled frame rejects at least 20 percent of 8,192 or more candidates, and disables it below 15 percent or below that calibrated work floor. Disabled cameras probe every 120 frames. The hysteresis and absolute candidate floor avoid paying for the depth pyramid in small or open views while retaining it in dense, strongly occluded views.

Other Looga renderers can register as depth consumers. Looga Terrain uses this contract so terrain patches and vegetation share one capture and one pyramid per camera.

Camera cuts, projection changes and configurable movement thresholds fail visible. Moving opaque occluders can expose geometry one frame before the matching depth history is available. It falls back to ordinary visibility for stereo, overlay cameras, partial viewports, unsupported camera types or missing or stale history. With no registered instance renderer or external consumer it does not enqueue the pass. Looga Terrain registers as an external consumer while terrain Hi-Z is enabled. Inactive camera histories are released automatically.

CaptureDiagnostics and ReadCameraDrawCounts are validation tools. GPU readbacks can stall rendering and allocate memory; keep them off during performance sampling. Report allocated GPU buffers separately from measured VRAM.

No universal speedup or production qualification is implied by enabling these options. Dense vegetation, authored wind, camera variants and installed tool combinations require their own acceptance checks.

## Optional baked box occlusion

Select solid Unity cube renderers, then use **Tools > Looga > Instancing > Bake Selected Box Occluders**.
The new BakedInstanceOcclusion component stores source references, transforms and a topology signature.
Save that component with its scene or prefab. Bake replaces its entries atomically; Invalidate clears them.
No source mesh, material or terrain asset is changed.

Only opaque URP Lit/Unlit built-in cubes qualify. Transparent, alpha-tested, property-block, LOD-managed,
hidden or changed sources are omitted. Custom blend/depth states are rejected. Each perspective camera
uses at most sixteen valid boxes on its culling layers. Orthographic views and shadows keep ordinary visibility.
An instance is rejected only when one box blocks all eight corners of its conservative bounds.
This is a small static-box cache, not an arbitrary-mesh visibility bake or a replacement for Hi-Z.
Do not use it as a proxy for walls with doors or holes. It can combine with Hi-Z, but measure the additional work.

## Measured release-candidate behavior

Full-opacity LOD endpoints use ordinary draw commands. Transitioning instances use separate packed-fade commands, including mirrored instances. Unity mesh-LOD ranges include the containing submesh offset.

The standard Unity DOTS shader contract stores LOD fade in a signed eight-bit value. SpeedTree percentage transitions therefore have lower precision than ordinary MeshRenderers. Static imported-tree geometry is verified, but exact native transition pixels are not promised. An official SpeedTree 8 model with authored wind and a billboard matches native color and normals. Its billboard also matches object-motion output. Small full-tree motion-buffer differences remain under investigation, so complete SpeedTree motion qualification is open. A model with bestWindQuality=0 cannot validate animated wind. Enable the material MotionVectors pass when animated wind motion is required; the renderer does not change this source setting. Apply runtime transform updates before camera rendering.

Windows/D3D12 measurements on an RTX 5080 show workload-dependent results. In a 100,000 simple-instance test at 1600x900, temporal Hi-Z added about 0.046 ms GPU time in an open ground view (0.729 ms versus 0.683 ms) and reduced an occluded view from about 0.686 ms to 0.126 ms. It reduced the occluded draw population from 76,834 to 3,488 instances without a screenshot difference. Keep it opt-in and measure the intended scene. Do not treat source-instance counts as simultaneously visible counts or allocated instance buffers as total VRAM.

The release candidate still has an open D3D12 validation gate also reproduced in an isolated native-only URP control. Do not interpret successful ordinary player runs as proof that the engine-level debug errors are resolved.

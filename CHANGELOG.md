# Changelog

## 0.1.0-preview.14

- Add `InstanceSet`. It draws static placements of several prototypes through one `InstanceRenderer`, so each view
  runs one culling callback instead of one for each container. `SetEntry(owner, prototype, placements)` replaces
  the instances of one entry and uploads once. Other entries do not change. Placements have no IDs.
- `VegetationWindField` gusts travel downwind as curved fronts instead of a fixed diagonal pattern. Add Gust Wavelength
  (default 24 m) and Lean (default 0.5), set with `ConfigureWaves`, and the `_LoogaFieldWave` shader globals. Lean bends
  part of the trunk amplitude steadily downwind, so all vegetation moves with the passing fronts. Displacement stays
  within the material amplitudes. Materials without a field keep their local wind.
- Add `VegetationGust` and `VegetationGustSheen` to the vegetation shader library. The sheen brightens and glosses the
  upper part of the plants while the same gust front bends them, most where they bend away from the camera. It runs in
  the fragment stage from the instance pivot, so it needs no extra interpolator. Zero sheen values change nothing.
- Add `ShaderLibrary` with the vegetation shader contract: `VegetationDeformation.hlsl` (wind, wind field, interaction
  and ground deformation), `VegetationLighting.hlsl` and the ground includes. They moved from Looga Terrain without
  a change in behavior, so every Looga package and project shader can use one implementation.
- `SceneInstanceProvider` finds nested source roots once per rebuild by walking each source's ancestors. The pairwise
  check was quadratic: one provider with 10,741 roots took 78 s to rebuild, now 24 s.
- Clear LOD transition histories on the GPU (`ClearHistory` kernel) instead of uploading a new `Vector4[Capacity]`.
  Editor views cut their history on most renders, so large grass populations allocated up to 113 MB in one frame
  and stalled on the copy. Culling shaders without the kernel fall back to a shared zero array.
- Add `SceneInstanceProvider.TrackSourceChanges` (default on). Off skips the per-frame source check, which read the
  hierarchy, transforms and material hashes of every root (about 90 ms per frame for 14,700 static roots).
- Fix prototype sharing for rotated or scaled copies. Part transforms now come from the local transforms between the root
  and the part, not from a world-space round trip. Before the fix, rounding gave each scattered copy (for example,
  MicroVerse object outputs) its own prototype and draw commands.
- Submit URP instance culling commands after culling and before shadow drawing.
- Scope deferred work to cameras using the active visibility feature. Release abandoned camera work.
- Keep dynamic view buffers, authored quality, and the compatibility path for other renderers.
- Maintain world-cell counts incrementally and use stable radix ordering for spatial groups.
- Run Morton ordering and stable radix sorting in a synchronous Burst direct call over pinned arrays, without changing spatial order policy or copying the population.
- Rebuild spatial order after storage trimming so live instances remain visible.
- Add grouped GPU appends for large populations, with compatibility kernels retained.
- Expose streaming CPU timing and GPU culling stage samples for performance diagnosis.
- Keep bounds in a default GPU buffer and upload changed ranges with SetData, avoiding repeated GPU reads from mapped upload storage.
- A controlled 11-frame full-world editor capture reduced Looga Instances.Cull median GPU time from 75.282 ms to 1.189 ms.
- Skip collider residency scans for containers outside every gameplay interest. Read resolved placements without a copy each frame.
- Share per-instance data between draw parts with the same mesh, local transform, color and lightmap. Mesh LOD levels of one renderer now use one data block.
- Add a static-transform renderer option. Static renderers keep no previous-frame matrices, and motion-vector metadata reads the current matrices.
- Track dirty slots in an ordered bitset instead of a sorted set. Merge nearby dirty ranges into one ring patch for each upload.
- Remove a per-slot array allocation from the upload scale calculation.
- Add stable distance density falloff (`DensityFalloffDistance`, `FarDensity`) for decorative camera views. Shadow views keep the full population.
- Static-transform renderers draw per-object motion vectors only for the finest LOD and mesh level.
- Fix mesh LOD selection for shared data blocks. Each draw part now reads its transform from its shared block.
  Before the fix, mesh LOD 1 read the inverse matrix, and near instances drew both mesh levels.
- Add GPU-resident renderers (`gpuResident` constructor option). The CPU tracks slot ranges only.
  `AllocateRange`, `WriteRanges` and `ReleaseRange` let a compute pass write transforms into instance storage.
  Culling, LOD selection and draw commands are unchanged. Capacity changes copy slots on the GPU.
- Compact GPU-resident storage when free slots below the high-water mark exceed a quarter of the live slots.
  A GPU pass moves written ranges together. Range handles use stable identifiers and stay valid.
- Create the BatchRendererGroup only while a renderer has storage, and release it when every population is empty.
  Unity calls every group for every culling view, so empty renderers now cost no culling callback.
- Add CPU profiler markers `Looga.Cull <content kind>` around each culling callback.

## [0.1.0-preview.13] - 2026-09-23

- Add per-prototype view range, view fade, LOD bias, and shadow mode policies.
- Support material-owned distance fade for tagged shaders and stable instance thinning for other shaders.
- Expose resident counts and on-demand unique camera visibility counts.
- Preserve source materials and placement buffers during quality changes.


## Unreleased

- Use a GPU-writable destination for compute-patched instance transforms. This fixes missing or stretched instances on Direct3D 12.

## 0.1.0-preview.12 - 2026-09-19

- Add an opt-in frame-time controller for derived instance quality.
- Preserve source placements, authored quality, native render scale, and gameplay collider residency.
- Add a shared target contract for terrain and vegetation quality coordination.

## 0.1.0-preview.11 - 2026-09-19

- Add persistent mapped dirty-range uploads with measured fallback bytes.
- Add compressed placement pages, bounded I/O and decompression, predictive prefetch, and residency limits.
- Coordinate Unity APV disk and blend work through its public streaming budgets.

## 0.1.0-preview.10 - 2026-09-19

- Add non-destructive forest-cell HLOD assets, deterministic card reduction, and a derived prefab baker.
- Add ownership-safe source-to-HLOD transitions and near, middle, and far shadow policies.
- Add a fixed-lighting impostor path with moving-sun invalidation and dynamic-light fallback.

## 0.1.0-preview.9 - 2026-09-19

- Add cross-card and octahedral impostor assets and a non-destructive prefab baker.
- Store albedo, object normals, and material masks for relightable impostor shading.
- Add wind, shadow, motion-vector, depth, and LOD transition passes.

## 0.1.0-preview.8 - 2026-09-19

- Add pooled near-field GameObject promotion with stable source and placement identity.
- Add collider, interaction, harvest, destruction, state, and network lifecycle hooks.
- Hide promoted BRG instances without replacing their stable handles.
- Return eligible proxies to their GPU representation through bounded residency work.

## 0.1.0-preview.7 - 2026-09-19

- Add immutable region and cell visibility above cached prototype clusters and instance bounds.
- Select direct, shared, hierarchical, and Hi-Z work from the visible candidate cost.
- Add coarse distance and frustum rejection with negative-cell-safe region grouping.
- Add an explicit super-batch API that merges compatible logical sources into one BRG population.
- Keep stable per-source ownership and release source instances without changing input prefabs.

## 0.1.0-preview.6 - 2026-09-19

- Add one shared visibility context and depth pyramid per eligible camera scope.
- Share prior-frame visibility across instances, terrain, future impostors, and HLODs.
- Add Game, Scene, camera-stack, render-scope, reflection, and preview camera policies.
- Share persistent depth resources across renderer-feature instances and skip duplicate captures.
- Keep stereo and unsupported camera types on visible fallback paths.

## 0.1.0-preview.5 - 2026-09-19

- Add deterministic structural prototype IDs and a ref-counted world registry shared by scene, painted, runtime, and terrain sources.
- Deduplicate compatible CPU prototype metadata across renderer owners while preserving source-specific material-profile and wind lifetimes.
- Add content-specific world cells for terrain surfaces, trees, details, grass, scene objects, painted instances, runtime objects, impostors, and HLODs.
- Aggregate prototype and instance occupancy by source, track residency, retain removal tombstones, and expose bounded revision-based dirty-region diagnostics.
- Publish each renderer's cells only after its source uploads complete and remove them when ownership is released.

## 0.1.0-preview.4 - 2026-09-19

- Add one read-only ownership status contract for scene, painted, TerrainData, MicroVerse and runtime adapters.
- Add a transactional runtime placement adapter that keeps the last complete revision when a producer is temporarily invalid.
- Report mixed Looga and native ownership explicitly; unsupported scene roots remain visible on their original renderers.
- Clear dedicated runtime targets when their adapter is disabled without changing source prefabs or application data.
- Add focused mixed-ownership, failed-revision and disable/restore tests.

## 0.1.0-preview.3 - 2026-09-19

- Add a non-destructive general prefab compiler with structured compatibility reports and extracted mesh, material, LOD, bounds, shadow, collider, wind and SpeedTree metadata.
- Reuse unchanged derived prototypes and invalidate only compiler entries that reference reimported meshes, materials, shaders, profiles or prefabs.
- Keep additional unsupported renderer types visible through their native path and expose those decisions in `SceneInstanceProvider` diagnostics.
- Preserve the existing `InstancePrototype.FromPrefab` API and route it through the compiler cache.
- Add focused multi-renderer, LOD, cache invalidation, fallback and SpeedTree metadata tests.

## 0.1.0-preview.2 - 2026-09-19

- Add per-camera adaptive temporal Hi-Z capture with rejection hysteresis and low-frequency probes.
- Fuse compatible multi-part culling into one two-dimensional compute dispatch.
- Morton-sort hierarchical dispatch order while preserving stable instance handles.

- Replace current-frame Hi-Z refinement with a prior-frame opaque-depth pyramid consumed during the initial visibility pass.
- Remove the forced depth/depth-normal prepass and second full instance-culling pass.
- Fail visible on stale history, camera cuts and projection changes; release inactive per-camera depth histories.
- Expose a shared consumer contract so Looga Terrain reuses the same temporal pyramid.
- Validate temporal Hi-Z on D3D12: 0.046 ms open-view overhead and 0.560 ms occluded-view savings in the 100,000-instance fixture.

- Reserve previous-transform metadata for moving SpeedTree batches before their first movement.
- Stop destroyed snapshot previews from retaining an editor update callback during scene changes.
- Reject known shader compiler errors before editor instance ownership; leave affected sources native.

- Declare the Unity Wind module for native SpeedTree wind in clean consumers.
- Record authored SpeedTree color, normal and billboard checks and the remaining motion-vector qualification limit.
- Fix full-opacity LOD draw buckets and preserve mirrored transition draws.
- Correct mesh-LOD index ranges for nonzero submeshes; add regression tests.

- Add optional conservative baked-box occlusion with source validation and saved-prefab persistence tests.
- Add decorative density and projected-size policies, stable visibility keys, and independent shadow range, LOD, cascade and fade controls.
- Add shared, hierarchical and cached-cluster selection. Keep direct culling as the default.
- Add optional current-frame URP depth occlusion, conservative camera fallbacks and explicit diagnostics.
- Declare the engine modules required by placement streaming and collision workflows.
- Tighten prototype sharing and failed quality updates; avoid empty compute dispatches.

## 0.1.0-preview.1

- Extract the existing general BRG renderer, containers, mesh authoring and streaming APIs from Looga Terrain.
- Preserve moved script GUIDs and serialized type migration information.
- Keep terrain adapters in Looga Terrain and shader/cache integration in Graphics Pro.
- Add owned material resource bindings for shared cache integrations.
- Add optional public spline exclusion updates with Undo and replacement support.
- Include independent dependency, authoring and hierarchy migration tests.

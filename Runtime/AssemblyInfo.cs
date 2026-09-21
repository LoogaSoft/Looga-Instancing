using System.Runtime.CompilerServices;

// The TerrainData adapter uses captured prototype geometry without another copy.
[assembly: InternalsVisibleTo("LoogaSoft.Terrain.Instances")]
[assembly: InternalsVisibleTo("LoogaSoft.Instancing.Universal")]

[assembly: InternalsVisibleTo("LoogaSoft.Instancing.EditorTests")]
[assembly: InternalsVisibleTo("LoogaSoft.Instancing.Editor")]

using System.Runtime.CompilerServices;

// The structural-emission tests assert an internal seam on SceneChangeFeed: the hierarchy-changed
// flag is set by an editor event that a synchronous EditMode test cannot raise.
[assembly: InternalsVisibleTo("SceneCollab.Editor.Tests")]

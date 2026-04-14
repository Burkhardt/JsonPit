using OsLib;
using System.Collections.Generic;
using System.IO;
namespace JsonPit
{
	/// <summary>
	/// PitFiles are files within a directory as used by JsonPit.
	/// They have a directory property and can be moved, copied, and deleted.
	/// They are used to store files in the JsonPit folder.
	/// Supported file extension is: pit.
	/// </summary>
	public class PitFile : CanonicalFile
	{
		
		// Supported file extensions
		public static readonly string[] SupportedExtensions = { "pit" };
		/// <summary>
		/// PitFile constructor
		/// </summary>
		/// <param name="fullName">Full path of a JsonPit, e.g., ~/Nations/ if the canonical Pit file is ~/Nations/Nations.pit</param>
		public PitFile(string fullName) : base(fullName)
		{
			if (string.IsNullOrEmpty(Ext)) Ext = "pit";
		}
		public PitFile(RaiPath path, string name) 
		: base(path, name, ext: "pit")
		{
			//ApplyPathConvention();
		}
		public int mv(PitFile src, bool replace = false, bool keepBackup = false) => base.mv(src, replace, keepBackup);
		/// <summary>
		/// Get all files within a specific JsonPit folder, optionally without the CanonicalFile
		/// this way, the CanonicalFile can be located in the same directory as all change files, 
		/// no need for a Changes subdirectory
		/// </summary>
		/// <param name="pitFolder"></param>
		/// <param name="excludeCanonicalFile">Exclude the canonical file from the list if true</param>
		/// <returns>fullPath of all files</returns>
		public IEnumerable<string> GetAllFiles(RaiPath pitFolder, bool excludeCanonicalFile = false)
		{
			foreach (string ext in SupportedExtensions)
			{
				foreach (var file in pitFolder.EnumerateFiles($"*.{ext}", SearchOption.AllDirectories))
				{
					if (!excludeCanonicalFile || file.FullName != FullName)
						yield return file.FullName;
				}
			}
		}
	}
}

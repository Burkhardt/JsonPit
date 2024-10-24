using OsLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace JsonPit
{
	/// <summary>
	/// PitFiles are files within a directory as used by JsonPit.
	/// They have a directory property and can be moved, copied, and deleted.
	/// They are used to store files in the JsonPit folder.
	/// Supported file extensions are: webp, png, jpeg, ogg, mov, xls, xlsm, docx, pdf.
	/// </summary>
	public class PitFile : CanonicalFile
	{
		// Supported file extensions
		public static readonly string[] SupportedExtensions = { "webp", "png", "jpeg", "ogg", "mov", "xls", "xlsm", "docx", "pdf", "json", "pit" };
		/// <summary>
		/// PitFile constructor
		/// </summary>
		/// <param name="fullName">Full path of a JsonPit, e.g., ~/Nations/ if the JsonPit json file is ~/Nations/Nations.json</param>
		/// <exception cref="InvalidOperationException"></exception>
		public PitFile(string fullName) : base(fullName)
		{
			var canName = new CanonicalFile(fullName);	// make sure the directory convention of CanonicalFile is followed
			if (!SupportedExtensions.Contains(Ext))
				Ext = "pit"; // default to pit
		}

		/// <summary>
		/// Get all files within a specific JsonPit folder, optionally without the CanonicalFile
		/// this way, the CanonicalFile can be located in the same directory as all change files, 
		/// no need for a Changes subdirectory
		/// </summary>
		/// <param name="pitFolder"></param>
		/// <param name="excludeCanonicalFile">Exclude the canonical file from the list if true</param>
		/// <returns>fullPath of all files</returns>
		public IEnumerable<string> GetAllFiles(string pitFolder, bool excludeCanonicalFile = false)
		{
			foreach (string ext in SupportedExtensions)
			{
				var filePaths = Directory.GetFiles(pitFolder, $"*.{ext}", SearchOption.AllDirectories);
				foreach (var filePath in filePaths)
				{
					if (!excludeCanonicalFile || filePath != FullName)
						yield return filePath;
				}
			}
		}
	}
}

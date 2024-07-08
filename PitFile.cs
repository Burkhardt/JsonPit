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
	public class PitFile : RaiFile
	{
		// Supported file extensions
		public static readonly string[] SupportedExtensions = { "webp", "png", "jpeg", "ogg", "mov", "xls", "xlsm", "docx", "pdf", "json" };
		/// <summary>
		/// PitFile constructor
		/// </summary>
		/// <param name="pitName">Name of a file to be located inside a JsonPit Nations/, e.g., Notes.pdf or Nations.json</param>
		/// <param name="jsonPitDirectory">Full path of a JsonPit, e.g., ~/Nations/ if the JsonPit json file is ~/Nations/Nations.json</param>
		/// <exception cref="InvalidOperationException"></exception>
		public PitFile(string pitName, string jsonPitDirectory) : base(jsonPitDirectory)
		{
			var rfName = new RaiFile(pitName);
			Name = rfName.Name;
			Ext = rfName.Ext;
			if (!SupportedExtensions.Contains(Ext))
				throw new InvalidOperationException("Unsupported document type for PitFile " + FullName + ".");

			var jsonPitFolder = new RaiFile(jsonPitDirectory);
			if (!jsonPitFolder.Path.Contains(jsonPitFolder.Name))   // somewhere in the path is fine
			{
				Path = jsonPitFolder.Path + jsonPitFolder.Name + Os.DIRSEPERATOR;
				cp(jsonPitFolder); // Copy the file to the JsonPit folder
			}
			else
			{
				Path = jsonPitFolder.Path;
				// cp(rfName); // Copy the file to the JsonPit folder => should be already here
			}
		}

		/// <summary>
		/// Constructor from a RaiFile; check for name conventions
		/// </summary>
		/// <param name="file"></param>
		/// <exception cref="InvalidOperationException"></exception>
		public PitFile(RaiFile file) : base(file.FullName)
		{
			if (!SupportedExtensions.Contains(Ext))
				throw new InvalidOperationException("Unsupported document type for PitFile " + FullName + ".");
			if (string.IsNullOrEmpty(Path))
				throw new InvalidOperationException("PitFile path cannot be empty for a PitFile; Needs to reside inside a PitFolder.");
		}
	
		// Get all files within a specific JsonPit folder
		public static List<RaiFile> GetAllFiles(string pitFolder)
		{
			List<RaiFile> files = new List<RaiFile>();

			foreach (string ext in SupportedExtensions)
			{
				var filePaths = Directory.GetFiles(pitFolder, $"*.{ext}", SearchOption.AllDirectories);
				files.AddRange(filePaths.Select(filePath => new RaiFile(filePath)));
			}

			return files;
		}
	}
}

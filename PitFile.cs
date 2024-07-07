using OsLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace JsonPit
{
	/// <summary>
	/// PitFiles are files within a directory as used by JsonPit.
	/// The have a directory property and can be moved, copied, and deleted.
	/// They are used to store files in the JsonPit folder.
	/// Supported file extensions are: webp, png, jpeg, ogg, mov, xls, xlsm, docx, pdf.
	/// </summary>
	public class PitFile : RaiFile
	{
		// Supported file extensions
		public static readonly string[] SupportedExtensions = { "webp", "png", "jpeg", "ogg", "mov", "xls", "xlsm", "docx", "pdf" };

		/// <summary>
		/// PitFile constructor
		/// </summary>
		/// <param name="name">name of a file to be located inside a JsonPit, e.g. Notes.pdf</param>
		/// <param name="jsonPitFile">full path of a JsonPit, i.e myJsonPit.FullPath, e.g. </param>
		/// <exception cref="InvalidOperationException"></exception>
		public PitFile(string name, string jsonPitFile) : base(new RaiPath(jsonPitFile).Path)
		{
			
			var rfName = new RaiFile(name);
			Name = rfName.Name;
			Ext = rfName.Ext;
			if (!SupportedExtensions.Contains(Ext))
				throw new InvalidOperationException("Unsupported document type for PitFile " + FullName + ".");
			var jsonPitFolder = new RaiFile(jsonPitFile);
			if (!jsonPitFolder.Path.Contains(jsonPitFolder.Name))   // somewhere in the path is fine
			{
				Path = jsonPitFolder.Path + jsonPitFolder.Name + Os.DIRSEPERATOR;
				cp(jsonPitFolder);	// copy the file to the JsonPit folder
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
			// no further checking for folder name compliance
		}

		// Move file to a new location within the JsonPit folder
		public int mv(RaiFile from)
		{
			return base.mv(from);
		}

		// Copy file to a new location within the JsonPit folder
		public int cp(RaiFile dest)
		{
			return base.cp(dest);
		}

		// Delete the file
		public new void rm()
		{
			base.rm();
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

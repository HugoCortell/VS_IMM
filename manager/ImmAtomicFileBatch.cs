#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace IntegratedModManager.Config;

public sealed class ImmAtomicFileBatch
{
	private readonly List<FileChange> Changes = new();
	private readonly HashSet<string> Paths = new(StringComparer.OrdinalIgnoreCase);

	public void Write(string path, string contents) { Add(path, contents); }

	public void Delete(string path) { Add(path, contents: null); }

	public bool TryCommit(out string error)
	{
		if (Changes.Count == 0) { error = ""; return true; }

		try { Prepare(); }
		catch (Exception exception)
		{
			CleanupAll();
			error = $"Failed to prepare configuration write: {exception.Message}";
			return false;
		}

		try
		{
			foreach (FileChange change in Changes)
			{
				if (change.Contents == null)
				{
					if (File.Exists(change.Path)) { File.Delete(change.Path); }
				}
				else
				{
					File.Move(change.TemporaryPath!, change.Path, overwrite: true);

					change.TemporaryPath = null;
				}

				change.Committed = true;
			}

			CleanupAll();
			error = "";
			return true;
		}
		catch (Exception exception)
		{
			string rollbackError = Rollback();

			CleanupTemporaryFiles();

			if (string.IsNullOrEmpty(rollbackError)) { CleanupBackups(); }

			error = $"Failed to commit configuration write: {exception.Message}";

			if (!string.IsNullOrEmpty(rollbackError)) { error += $" Rollback also failed: {rollbackError}"; }

			return false;
		}
	}

	private void Add(string path, string? contents)
	{
		string fullPath = Path.GetFullPath(path);

		if (!Paths.Add(fullPath))
		{
			throw new InvalidOperationException($"Configuration file '{fullPath}' was added to the same write batch more than once.");
		}

		Changes.Add(new FileChange(fullPath, contents));
	}

	private void Prepare()
	{
		foreach (FileChange change in Changes)
		{
			string? directory = Path.GetDirectoryName(change.Path);

			if (change.Contents != null && !string.IsNullOrEmpty(directory)) { Directory.CreateDirectory(directory); }

			change.OriginalExisted = File.Exists(change.Path);

			if (change.OriginalExisted)
			{
				change.BackupPath = change.Path + ".imm-backup-" + Guid.NewGuid().ToString("N");

				File.Copy(change.Path, change.BackupPath, overwrite: false);
			}

			if (change.Contents != null)
			{
				change.TemporaryPath = change.Path + ".imm-write-" + Guid.NewGuid().ToString("N");

				File.WriteAllText(change.TemporaryPath, change.Contents);
			}
		}
	}

	private string Rollback()
	{
		List<string> errors = new();

		foreach (FileChange change in Changes.Where(change => change.Committed).Reverse())
		{
			try
			{
				if (change.OriginalExisted)
				{
					if (string.IsNullOrEmpty(change.BackupPath) || !File.Exists(change.BackupPath))
					{
						throw new IOException($"Backup for '{change.Path}' was unavailable.");
					}

					File.Copy(change.BackupPath, change.Path, overwrite: true);
				}
				else if (File.Exists(change.Path)) { File.Delete(change.Path); }
			}
			catch (Exception exception) { errors.Add($"{change.Path}: {exception.Message}"); }
		}

		return string.Join("; ", errors);
	}

	private void CleanupTemporaryFiles()
	{
		foreach (FileChange change in Changes) { TryDelete(change.TemporaryPath); }
	}

	private void CleanupBackups()
	{
		foreach (FileChange change in Changes) { TryDelete(change.BackupPath); }
	}

	private void CleanupAll()
	{
		CleanupTemporaryFiles();
		CleanupBackups();
	}

	private static void TryDelete(string? path)
	{
		if (string.IsNullOrEmpty(path) || !File.Exists(path)) { return; }

		try { File.Delete(path); }
		catch { } // Cleanup is best effort. The original commit/rollback error is more useful than masking it with a temporary-file error.
	}

	private sealed class FileChange
	{
		public string Path { get; }
		public string? Contents { get; }

		public bool OriginalExisted;
		public bool Committed;
		public string? TemporaryPath;
		public string? BackupPath;

		public FileChange(string path, string? contents)
		{
			Path = path;
			Contents = contents;
		}
	}
}

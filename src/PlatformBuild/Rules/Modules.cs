﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using PlatformBuild.Crap;
using PlatformBuild.FileSystem;
using PlatformBuild.LogOutput;

namespace PlatformBuild.Rules
{
	public class Modules : IModules
	{
		readonly FilePath _moduleRulePath;
		readonly IFileSystem _files;
		readonly IPatterns _patterns;

		/// <summary>
		/// Repository urls. Indexes are same as the target filesystem paths
		/// </summary>
		public string[] Repos { get; set; }

		/// <summary>
		/// Filesystem paths. Indexes are same as the source repository url.
		/// </summary>
		public string[] Paths { get; set; }
		public List<int>[] Deps { get; set; }

		public Modules(FilePath moduleRulePath, IFileSystem files, IPatterns patterns)
		{
			_moduleRulePath = moduleRulePath;
			_files = files;
			_patterns = patterns;
		}

		/// <summary> (1)
		/// go find the build folder for each module.
		/// match in paths, write index to deps
		/// </summary>
		public void ReadDependencies(FilePath rootPath)
		{
			bool missingModule = false;
			string lastMissingModule = "";
			string lastFailedPath = "";

			ReadModules(_moduleRulePath);
			for (int i = 0; i < Paths.Length; i++)
			{
				var path = Paths[i];
				var buildRuleFile = DependencyRulePath(rootPath, path);
				if (!_files.Exists(buildRuleFile)) { continue; }

				var lines = _files.Lines(buildRuleFile);
				foreach (var line in lines)
				{
					var depRef = Paths.Index(line);
					if (depRef < 0)
					{
						lastMissingModule = line;
						lastFailedPath = path;
						missingModule = true;
					}
					Deps[i].Add(Paths.Index(line));
				}
			}

			if (missingModule)
			{
				throw new UnknownModuleException(lastFailedPath + " requires unknown module " + lastMissingModule);
			}
		}

		/// <summary> (2)
		/// put modules in order, so we can build and distribute without
		/// getting any out-of-date libraries
		/// </summary>
		public void SortInDependencyOrder()
		{
			var @in = Enumerable.Range(0, Paths.Length).ToList();
			var @out = new List<int>();
			// go simple. scan @in, if @out contains, remove&append
			// if no change in a whole loop, then circular dependency.

			var noLoops = true;
			while (noLoops)
			{
				noLoops = false;
				for (int i = 0; i < @in.Count; i++)
				{
					if (SelfReferencing(@in[i])) throw new Exception(Paths[@in[i]] + " is self referencing");
					if (CanAdd(Deps[@in[i]], @out))
					{
						@out.Add(@in[i]);
						@in.RemoveAt(i);
						noLoops = true;
						break;
					}
				}
			}
			if (@in.Count > 0)
				throw new Exception("Circular dependency. In: "
					+ string.Join(", ", @in.Select(ix => Paths[ix]))
					+ "\r\nOut: " + string.Join(", ", @out.Select(ix => Paths[ix]))
					);

			var newRepos = new List<string>();
			var newPaths = new List<string>();
			var newDeps = new List<List<int>>();

			foreach (var idx in @out)
			{
				newRepos.Add(Repos[idx]);
				newPaths.Add(Paths[idx]);
				newDeps.Add(Deps[idx]);
			}

			Repos = newRepos.ToArray();
			Log.Info("Build order: "+ string.Join(", ", Repos));

			Paths = newPaths.ToArray();
			Deps = newDeps.ToArray();
		}

		bool SelfReferencing(int idx)
		{
			var selfName = Paths[idx];
			var depNames = Deps[idx].Select(i => Paths[i]).ToArray();

			Log.Verbose(selfName + " <-- " + string.Join(", ", depNames));
			return depNames.Contains(selfName);
		}

		/// <summary> (3)
		/// Build a set of locks so builds don't get ahead of git-pulls
		/// </summary>
		public IList<AutoResetEvent> CreateAndSetLocks()
		{
			var l = new List<AutoResetEvent>();
			for (int i = 0; i < Paths.Length; i++)
			{
				l.Add(new AutoResetEvent(false));
			}
			return l;
		}

		static bool CanAdd(IEnumerable<int> required, ICollection<int> available)
		{
			return required.All(available.Contains);
		}

		FilePath DependencyRulePath(FilePath filePath, string path)
		{
			return filePath.Navigate((FilePath)path)
				.Navigate(_patterns.DependencyPath).Navigate((FilePath)"Depends.rule");
		}

		void ReadModules(FilePath filePath)
		{
			Log.Verbose("Reading " + filePath.ToEnvironmentalPath());
			var lines = _files.Lines(filePath);

			var c = lines.Length;

			Repos = new string[c]; // src id => src repo
			Paths = new string[c]; // src id => file path
			Deps = new List<int>[c]; // src id => dst


			for (int i = 0; i < c; i++)
			{
				Log.Verbose(lines[i]);
				var bits = lines[i].Split('=').Select(s => s.Trim()).ToArray();

				Repos[i] = bits[1];
				Paths[i] = bits[0];
				Deps[i] = new List<int>();
			}
		}
	}
}
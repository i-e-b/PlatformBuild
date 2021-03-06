﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using PlatformBuild.CmdLineProxies;
using PlatformBuild.DependencyManagement;
using PlatformBuild.FileSystem;
using PlatformBuild.LogOutput;
using PlatformBuild.Rules;

namespace PlatformBuild
{
	public class Builder
	{
		readonly IFileSystem _files;
		readonly IGit _git;
		readonly IDependencyManager _depMgr;
		readonly IRuleFactory _rules;
		readonly IBuildCmd _builder;
		FilePath _rootPath;
		public IModules Modules { get; private set; }
		IList<AutoResetEvent> _locks;
		IPatterns _patterns;

		public Builder(IFileSystem files, IGit git, IDependencyManager depMgr, IRuleFactory rules, IBuildCmd builder)
		{
			_files = files;
			_git = git;
			_depMgr = depMgr;
			_rules = rules;
			_builder = builder;
		}

		public void Prepare()
		{
			_rootPath = _files.GetPlatformRoot();
			Log.Status("Started in " + _rootPath.ToEnvironmentalPath() + "; Updating self");

			_git.PullMaster(_rootPath);

			_patterns = _rules.GetRulePatterns();
			Modules = _rules.GetModules();
			try
			{
				Modules.ReadDependencies(_rootPath);
			}
			catch (UnknownModuleException ex)
			{
				Log.Error(ex.Message + ", will pull all repositories. You will need to retry platform build.");

				_locks = Modules.CreateAndSetLocks();
				PullRepos();
				CloneMissingRepos();

				throw;
			}
			Modules.SortInDependencyOrder();

			_depMgr.ReadMasters(_rootPath, _patterns.Masters);

			DeleteOldPaths();

			Log.Verbose("Processing " + string.Join(", ", Modules.Paths));
			CloneMissingRepos();

			_locks = Modules.CreateAndSetLocks();
		}

		void DeleteOldPaths()
		{
			Log.Verbose("Deleting old paths");
			foreach(var path in _rules.GetPathsToDelete())
			{
				if (! _files.Exists(path)) continue;
					
				try
				{
					_files.DeletePath(path);
				} catch (Exception ex)
				{
					Log.Error("Failed to delete " + path.ToEnvironmentalPath() + ", because of a " + ex.GetType());
				}
			}
		}

		public void RunBuild(bool runDatabases)
		{
			Thread databases = null;
			if (runDatabases)
			{
				databases = new Thread(RebuildDatabases);
				databases.Start();
			}

			var pulling = new Thread(PullRepos);
			var building = new Thread(GetDependenciesAndBuild);

			pulling.Start();
			building.Start();

			building.Join();
			Log.Status("All builds finished");

			if (runDatabases)
			{
				databases.Join();
				Log.Status("All databases updated");
			}
		}

		public void PullRepos()
		{
			for (int i = 0; i < Modules.Paths.Length; i++)
			{
				var modulePath = _rootPath.Navigate((FilePath)Modules.Paths[i]);
				var libPath = modulePath.Navigate((FilePath)"lib");

				if (_files.Exists(libPath)) _git.CheckoutFolder(libPath);
				_git.PullCurrentBranch(modulePath);
				Log.Status("Updated " + Modules.Paths[i]);
				_locks[i].Set();
			}
		}

		public void GetDependenciesAndBuild()
		{
			for (int i = 0; i < Modules.Paths.Length; i++)
			{
				var moduleName = Modules.Paths[i];
				var buildPath = _rootPath.Navigate((FilePath)(moduleName));
				var libPath = _rootPath.Navigate((FilePath)(moduleName)).Navigate(_patterns.DependencyPath);
				var srcPath = _rootPath.Navigate((FilePath)(moduleName + "/src"));

				if (!_locks[i].WaitOne(TimeSpan.FromSeconds(1)))
				{
					Log.Info("Waiting for git update of " + moduleName);
					if (!_locks[i].WaitOne(TimeSpan.FromSeconds(30)))
					{
						Log.Error("Waiting a long time for " + moduleName + " to update!");
						_locks[i].WaitOne();
					}
				}

				CopyAvailableDependenciesToDirectory(libPath);

				if (!_files.Exists(buildPath))
				{
					Log.Info("Ignoring " + moduleName + " because it has no build folder");
					continue;
				}

				Log.Info("Starting build of " + moduleName);
				try
				{
					int code = _builder.Build(_rootPath, buildPath);
					if (code != 0) Log.Error("Build failed: " + moduleName);
					else Log.Status("Build complete: " + moduleName);
				}
				catch (Exception ex)
				{
					Log.Error("Build error: " + ex.GetType() + ": " + ex.Message);
				}

				_depMgr.UpdateAvailableDependencies(srcPath);
			}
		}

		public void RebuildDatabases()
		{
			var uniquePaths = Deduplicate(Modules.Paths, Modules.Repos);

			foreach (var path in uniquePaths)
			{
				var projPath = _rootPath.Navigate((FilePath)path);

				var runMigrationsLocally = projPath.Append(new FilePath("RunMigrationsLocally.ps1"));
				if (_files.Exists(runMigrationsLocally))
					RebuildByFluentMigration(projPath, runMigrationsLocally);
				else
					RebuildByScripts(projPath);
			}
		}

		/// <summary>
		/// Remove paths with duplicated source repos.
		/// Remove paths that have DatabaseScripts added by sub-repo
		/// </summary>
		IEnumerable<string> Deduplicate(string[] paths, string[] repos)
		{
			var parents = paths.Where(p=>p.ToLower().EndsWith("/databasescripts")).Select(p=>TruncRight(p, 16)).ToList();
			var seen = new List<string>();
			var outp = new List<string>();

			for (int i = 0; i < paths.Length; i++)
			{
				if (seen.Contains(repos[i])) continue;
				if (parents.Contains(paths[i])) continue;
				seen.Add(repos[i]);
				outp.Add(paths[i]);
			}
			return outp;
		}

		string TruncRight(string s, int i)
		{
			return s.Substring(0, s.Length-i);
		}

		private void RebuildByFluentMigration(FilePath projPath, FilePath psScript)
		{
			var createDatabase = projPath.Append(new FilePath("DatabaseScripts")).Append(new FilePath("CreateDatabase.sql"));
			Log.Status("Creating database from " + createDatabase.ToEnvironmentalPath());
			_builder.RunSqlScripts(projPath, createDatabase);

			Log.Status("Running RunMigrationsLocally.ps1");
			projPath.Call("powershell " + psScript.ToEnvironmentalPath(), "");
		}

		private void RebuildByScripts(FilePath projPath)
		{
			var dbPath = projPath.Navigate((FilePath) "DatabaseScripts");
			var selfPath = projPath.Navigate((FilePath)"../DatabaseScripts");
			var sqlSpecificPath = dbPath.Navigate((FilePath) "SqlServer");

			if (!_files.Exists(dbPath))
			{
				if (_files.Exists(selfPath)) dbPath = selfPath;
				else return;
			}

			Log.Status("Scripts from " + dbPath.ToEnvironmentalPath());

			var finalSrcPath = (_files.Exists(sqlSpecificPath)) ? (sqlSpecificPath) : (dbPath);

			foreach (var file in _files.SortedDescendants(finalSrcPath, "*.sql"))
			{
				Log.Verbose(file.LastElement());
				_builder.RunSqlScripts(projPath, file);
			}
		}

		void CopyAvailableDependenciesToDirectory(FilePath moduleLibPath)
		{
			var dest = _rootPath.Navigate(moduleLibPath);
			_depMgr.CopyBuildResultsTo(dest);
		}

		void CloneMissingRepos()
		{
			for (int i = 0; i < Modules.Paths.Length; i++)
			{
				var path = new FilePath(Modules.Paths[i]);
				var expected = _rootPath.Navigate(path);
				if (_files.Exists(expected)) continue;

				Log.Info(path.ToEnvironmentalPath() + " is missing. Cloning...");
				_git.Clone(_rootPath, expected, Modules.Repos[i]);
			}
		}
	}
}
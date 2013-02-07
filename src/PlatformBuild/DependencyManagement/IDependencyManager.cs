using System.IO;

namespace PlatformBuild.DependencyManagement
{
	public interface IDependencyManager
	{
		void CopyBuildResultsTo(FilePath dest);
		void UpdateAvailableDependencies(FilePath srcPath);
	}
}
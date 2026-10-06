using System;

namespace Supervertaler.Core.Tests
{
    /// <summary>
    /// Where memory banks and prompts come from. A team folder set in config.json
    /// is used only when it answers; otherwise the session falls back to the
    /// user's own data folder and says why - never silently.
    /// </summary>
    [Tests]
    internal static class TeamFolderTests
    {
        private const string Root = @"C:\Users\someone\Supervertaler";
        private const string Team = @"\\server\share\Supervertaler-team";

        public static void NoTeamFolder_UsesTheRoot()
        {
            foreach (var none in new[] { null, "", "   " })
            {
                var d = SupervertalerPaths.DecideContentRoot(Root, none, _ => throw new Exception("must not be asked"));
                Assert.Equal(Root, d.ContentRoot, "root");
                Assert.True(d.TeamFolder == null, "no team folder");
                Assert.True(d.Problem == null, "no problem");
            }
        }

        public static void AReachableTeamFolder_IsUsed()
        {
            var d = SupervertalerPaths.DecideContentRoot(Root, " " + Team + " ", p => p == Team);
            Assert.Equal(Team, d.ContentRoot, "team folder, trimmed");
            Assert.Equal(Team, d.TeamFolder, "reported");
            Assert.True(d.Problem == null, "no problem");
        }

        public static void AMissingTeamFolder_FallsBackAndSaysSo()
        {
            var d = SupervertalerPaths.DecideContentRoot(Root, Team, _ => false);
            Assert.Equal(Root, d.ContentRoot, "own folder");
            Assert.Equal(Team, d.TeamFolder, "still reported as configured");
            Assert.True(d.Problem != null && d.Problem.Contains("cannot be found"), "says why: " + d.Problem);
        }

        public static void ATeamFolderThatDoesNotAnswer_FallsBackAndSaysSo()
        {
            var d = SupervertalerPaths.DecideContentRoot(Root, Team, _ => null);
            Assert.Equal(Root, d.ContentRoot, "own folder");
            Assert.True(d.Problem != null && d.Problem.Contains("did not answer"), "says why: " + d.Problem);
        }

        public static void ARelativeTeamFolder_IsRefused()
        {
            var d = SupervertalerPaths.DecideContentRoot(Root, @"team\banks", _ => true);
            Assert.Equal(Root, d.ContentRoot, "own folder");
            Assert.True(d.Problem != null && d.Problem.Contains("not a full path"), "says why: " + d.Problem);
        }
    }
}

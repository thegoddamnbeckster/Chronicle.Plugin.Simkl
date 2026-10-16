using Chronicle.Plugin.Simkl;
using Xunit;

namespace Chronicle.Plugin.Simkl.Tests;

public class ExternalIdPolicyTests
{
    [Fact]
    public void TheProviderDeclaresHowItsIdsBehave()
    {
        var p = new SimklMetadataProvider().GetExternalIdPolicy();
        Assert.Equal("simkl", p.Source);
        Assert.Matches(p.RefusedIdPattern!, "simkl:123");          // the untyped form this plugin cannot parse
        Assert.DoesNotMatch(p.RefusedIdPattern!, "simkl:movie:123");
        Assert.DoesNotMatch(p.RefusedIdPattern!, "simkl:123:s1e2");
        Assert.Equal("simkl:anime:{id}", p.CrossRefFormats["anime"]);
    }
}

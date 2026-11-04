using DrinksEcommerce.Application;
using DrinksEcommerce.Domain.Common;
using DrinksEcommerce.Infrastructure;
using DrinksEcommerce.Persistence;
using FluentAssertions;

namespace DrinksEcommerce.ArchitectureTests;

public class CleanArchitectureTests
{
    [Fact]
    public void Domain_Should_Not_Have_Dependencies_On_Outer_Layers()
    {
        var domainAssembly = typeof(BaseEntity).Assembly;
        var referencedNames = domainAssembly.GetReferencedAssemblies().Select(a => a.Name).ToList();

        referencedNames.Should().NotContain("DrinksEcommerce.Application");
        referencedNames.Should().NotContain("DrinksEcommerce.Infrastructure");
        referencedNames.Should().NotContain("DrinksEcommerce.Persistence");
        referencedNames.Should().NotContain("DrinksEcommerce.API");
    }

    [Fact]
    public void Application_Should_Only_Depend_On_Domain_Not_Outer_Layers()
    {
        var appAssembly = typeof(DrinksEcommerce.Application.DependencyInjection).Assembly;
        var referencedNames = appAssembly.GetReferencedAssemblies().Select(a => a.Name).ToList();

        referencedNames.Should().Contain("DrinksEcommerce.Domain");
        referencedNames.Should().NotContain("DrinksEcommerce.Infrastructure");
        referencedNames.Should().NotContain("DrinksEcommerce.Persistence");
        referencedNames.Should().NotContain("DrinksEcommerce.API");
    }

    [Fact]
    public void Infrastructure_And_Persistence_Should_Not_Depend_On_API()
    {
        var infraRefs = typeof(DrinksEcommerce.Infrastructure.DependencyInjection).Assembly
            .GetReferencedAssemblies().Select(a => a.Name).ToList();
        var persistRefs = typeof(DrinksEcommerce.Persistence.DependencyInjection).Assembly
            .GetReferencedAssemblies().Select(a => a.Name).ToList();

        infraRefs.Should().NotContain("DrinksEcommerce.API");
        persistRefs.Should().NotContain("DrinksEcommerce.API");
    }
}

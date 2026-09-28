using System;
using Shouldly;
using Xunit;

namespace GalaxyData.Common.Tests;

public sealed class DynamicNodeTests
{
   [Fact]
   public void AsScalarReturnsDecimals()
   {
      new DynamicNode(1.25m).AsScalar().ShouldBe(1.25m);
   }

   [Fact]
   public void AsScalarReturnsGuids()
   {
      Guid id = Guid.NewGuid();
      new DynamicNode(id).AsScalar().ShouldBe(id);
   }

   [Fact]
   public void AsScalarReturnsDateTimeOffsets()
   {
      DateTimeOffset at = new(2026, 9, 28, 13, 45, 0, TimeSpan.FromHours(2));
      new DynamicNode(at).AsScalar().ShouldBe(at);
   }

   [Fact]
   public void AsScalarReturnsNullForNull()
   {
      DynamicNode.Null.AsScalar().ShouldBeNull();
   }
}

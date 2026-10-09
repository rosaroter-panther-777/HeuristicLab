using System.IO;
using HEAL.Attic;
using HeuristicLab.Core;
using HeuristicLab.Data;
using HeuristicLab.Random;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Tests {
  /// <summary>
  /// Smoke test for .hl persistence on modern .NET: HEAL.Attic 1.5.0 failed here at the first
  /// (de)serialization because it registers a CoreLib type removed in .NET 5.
  /// </summary>
  [TestClass]
  public class AtticRoundTripTest {
    [TestMethod]
    [TestCategory("Essential")]
    [TestCategory("Persistence")]
    public void CoreItemsAndRandomStateRoundTrip() {
      var items = new ItemList<IItem> {
        new IntValue(42), new DoubleValue(3.5), new StringValue("hl"),
        new DoubleMatrix(new double[,] { { 1, 2 }, { 3, 4 } })
      };
      var random = new MersenneTwister(1234);
      random.NextDouble();

      var path = Path.GetTempFileName();
      try {
        var serializer = new ProtoBufSerializer();
        serializer.Serialize(new object[] { items, random }, path);
        var restored = (object[])serializer.Deserialize(path);

        var restoredItems = (ItemList<IItem>)restored[0];
        Assert.AreEqual(4, restoredItems.Count);
        Assert.AreEqual(42, ((IntValue)restoredItems[0]).Value);
        Assert.AreEqual(3.5, ((DoubleValue)restoredItems[1]).Value);
        Assert.AreEqual("hl", ((StringValue)restoredItems[2]).Value);
        Assert.AreEqual(4.0, ((DoubleMatrix)restoredItems[3])[1, 1]);

        var restoredRandom = (MersenneTwister)restored[1];
        for (int i = 0; i < 100; i++)
          Assert.AreEqual(random.NextDouble(), restoredRandom.NextDouble());
      } finally {
        File.Delete(path);
      }
    }
  }
}

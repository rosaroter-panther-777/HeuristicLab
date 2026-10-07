using System;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Tests {
  /// <summary>
  /// On modern .NET a failed Debug.Assert or Contract.Assert terminates the process (FailFast),
  /// aborting the whole test run. The legacy suite ran against Release builds where these are
  /// compiled out; here the libraries are built in Debug, so failures are turned into exceptions
  /// that surface as ordinary test failures (or satisfy tests expecting an exception).
  /// </summary>
  [TestClass]
  public static class AssertionsAsExceptions {
    [AssemblyInitialize]
    public static void AssemblyInitialize(TestContext testContext) {
      Contract.ContractFailed += (sender, e) => e.SetUnwind();
      Trace.Listeners.Clear();
      Trace.Listeners.Add(new ThrowingTraceListener());
    }

    private sealed class ThrowingTraceListener : TraceListener {
      public override void Fail(string message, string detailMessage) {
        throw new AssertionFailedException(message + (string.IsNullOrEmpty(detailMessage) ? "" : " " + detailMessage));
      }
      public override void Write(string message) { }
      public override void WriteLine(string message) { }
    }
  }

  public sealed class AssertionFailedException : Exception {
    public AssertionFailedException(string message) : base(message) { }
  }

  [TestClass]
  public class AssertionsAsExceptionsTest {
    [TestMethod]
    [TestCategory("Essential")]
    public void DebugAssertThrows() {
      Assert.ThrowsExactly<AssertionFailedException>(() => Debug.Assert(false, "probe"));
    }

    [TestMethod]
    [TestCategory("Essential")]
    public void ContractAssertThrows() {
      try {
        Contract.Assert(false, "probe");
        Assert.Fail("expected exception");
      } catch (AssertFailedException) {
        throw;
      } catch (Exception) {
      }
    }
  }
}

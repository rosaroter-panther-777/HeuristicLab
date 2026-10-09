using System.Linq;
using HeuristicLab.Operators.Programmable;
using HeuristicLab.Scripting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HeuristicLab.Tests {
  /// <summary>
  /// Scripts and programmable operators compile C# at runtime. Legacy CSharpCodeProvider throws
  /// PlatformNotSupportedException on .NET Core; the ports compile with Roslyn instead.
  /// </summary>
  [TestClass]
  public class RuntimeCompilationTest {
    [TestMethod]
    [TestCategory("Essential")]
    [TestCategory("Scripting")]
    public void CSharpScriptCompilesAndRuns() {
      var script = new CSharpScript(@"
using System.Linq;
public class TestScript : HeuristicLab.Scripting.CSharpScriptBase {
  public override void Main() {
    vars.answer = Enumerable.Range(1, 6).Sum() * 2;  // dynamic: needs the runtime binder
  }
}");
      script.Compile();
      script.Execute();
      Assert.AreEqual(42, script.VariableStore["answer"]);
    }

    [TestMethod]
    [TestCategory("Essential")]
    [TestCategory("Scripting")]
    public void CSharpScriptReportsCompileErrors() {
      var script = new CSharpScript("public class Broken { int x = ; }");
      Assert.ThrowsExactly<CompilationException>(() => script.Compile());
      Assert.IsTrue(script.CompileErrors.Cast<System.CodeDom.Compiler.CompilerError>().Any(e => !e.IsWarning && e.Line == 1));
    }

    [TestMethod]
    [TestCategory("Essential")]
    [TestCategory("Operators.Programmable")]
    public void ProgrammableOperatorCompiles() {
      var op = new ProgrammableOperator { Code = "return null;" };
      op.Compile();
      Assert.IsFalse(op.CompileErrors.HasErrors, string.Join("\n", op.CompileErrors.Cast<System.CodeDom.Compiler.CompilerError>().Select(e => e.ErrorText)));
    }
  }
}

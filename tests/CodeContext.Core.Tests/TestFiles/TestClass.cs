namespace CodeContext.Core.Tests.TestFiles
{
    public class MyBaseClass
    {
    }

    public interface ITest
    {
        void MyMethod();
    }

    public class TestClass : MyBaseClass, ITest
    {
        public string? MyProperty { get; set; }

        public void MyMethod()
        {
        }

        public void AnotherMethod()
        {
            MyMethod();
        }
    }
}

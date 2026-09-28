namespace GalaxyData.Query.Binding;

public enum UnaryOp : byte
{
   Negate,
   Plus,
   Not,
}

public enum BinaryOp : byte
{
   Add,
   Subtract,
   Multiply,
   Divide,
   Modulo,
   Concat,
   Equal,
   NotEqual,
   Less,
   LessOrEqual,
   Greater,
   GreaterOrEqual,
   And,
   Or,
}

public static class OperatorText
{
   public static string Symbol(UnaryOp op) => op switch
   {
      UnaryOp.Negate => "-",
      UnaryOp.Plus => "+",
      UnaryOp.Not => "not ",
      _ => op.ToString(),
   };

   public static string Symbol(BinaryOp op) => op switch
   {
      BinaryOp.Add => "+",
      BinaryOp.Subtract => "-",
      BinaryOp.Multiply => "*",
      BinaryOp.Divide => "/",
      BinaryOp.Modulo => "%",
      BinaryOp.Concat => "+",
      BinaryOp.Equal => "==",
      BinaryOp.NotEqual => "!=",
      BinaryOp.Less => "<",
      BinaryOp.LessOrEqual => "<=",
      BinaryOp.Greater => ">",
      BinaryOp.GreaterOrEqual => ">=",
      BinaryOp.And => "and",
      BinaryOp.Or => "or",
      _ => op.ToString(),
   };

   public static bool IsComparison(BinaryOp op) => op is BinaryOp.Equal or BinaryOp.NotEqual or BinaryOp.Less
      or BinaryOp.LessOrEqual or BinaryOp.Greater or BinaryOp.GreaterOrEqual;
}

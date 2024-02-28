namespace JIM.Models.Extensibility
{
    public class FunctionParameter
    {
        public int Id { get; set; }

        public Function Function { get; set; } = null!;
        
        /// <summary>
        /// What position in the function signature is this parameter intended for?
        /// </summary>
        public int Position { get; set; }

        public FunctionParameterType Type { get; set; }
    }
}

using JIM.Models.Core;
using JIM.Models.Extensibility;
using JIM.Models.Staging;

namespace JIM.Models.Logic
{
    /// <summary>
    /// A SyncRuleMappingSourceParamValue supports the calling of a function as part of a sync rule mapping.
    /// A SyncRuleMappingSourceParamValue can get it's value from an attribute, or a constant value from type-specific properties.
    /// If it is an attribute, then depending on the direction of the sync rule (import/export), then it'll be either the 
    /// ConnectedSystemAttribute or MetaverseAttribute that needs to be populated.
    /// </summary>
    public class SyncRuleMappingSourceParamValue
    {
        public int Id { get; set; }

        /// <summary>
        /// The name to give the parameter. This will be the ConnectedSystemAttribute or MetaverseAttribute name copied over, 
        /// or if a constant value is used, then an auto-generated name will be assigned, or the user can override with a customer name.
        /// The name is then made available to custom expressions as variables for them to use.
        /// </summary>
        public string Name { get; set; } = null!;
        
        /// <summary>
        /// Relates this param value to a defined parameter on a Function.
        /// Can be null if Expressions are being used instead of Functions.
        /// </summary>
        public FunctionParameter? FunctionParameter { get; set; } = null!;

        // todo: add in support for Expressions (alternative to Functions)

        /// <summary>
        /// For export only: A Metaverse Attribute can be used as the source for this parameter.
        /// </summary>
        public MetaverseAttribute? MetaverseAttribute { get; set; }

        /// <summary>
        /// For import only: A Connected System Attribute can be used as the source for this parameter.
        /// </summary>
        public ConnectedSystemObjectTypeAttribute? ConnectedSystemAttribute { get; set; }

        /// <summary>
        /// Holds a constant string to use as the value of the param.
        /// </summary>
        public string? StringValue { get; set; }

        /// <summary>
        /// Holds a constant datetime to use as the value of the param.
        /// </summary>
        public DateTime DateTimeValue { get; set; }

        /// <summary>
        /// Holds a constant double number to use as the value of the param.
        /// </summary>
        public double DoubleValue { get; set; }

        /// <summary>
        /// Holds a constant integer number to use as the value of the param.
        /// </summary>
        public int IntValue { get; set; }

        /// <summary>
        /// Holds a constant boolean to use as the value of the param.
        /// </summary>
        public bool BoolValue { get; set; }
    }
}

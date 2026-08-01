using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
namespace ObjektRT.Core
{
    public interface IValue
    {
        object? GetObjectData();
    }

    /// <summary>
    /// Represents a generic container that encapsulates a single value of the specified type.
    /// </summary>
    /// <remarks>Use this class to wrap a value of any type in a consistent container, which can be useful for
    /// scenarios such as generic data handling, value transformation, or API responses. The class provides methods for
    /// equality comparison and string conversion based on the underlying value.</remarks>
    /// <typeparam name="T">The type of the value to be encapsulated.</typeparam>
    public class Value<T> : IValue
    {
        public T Data { get; set; }
        /// <summary>
        /// Initializes a new instance of the Value class with the specified data.
        /// </summary>
        /// <param name="data">The data to be stored in the value. This parameter is assigned to the Data property.</param>
        public Value(T data)
        {
            Data = data;
        }

        public object? GetObjectData() => Data;

        /// <summary>
        /// Converts the data contained in the specified value to its string representation.
        /// </summary>
        /// <param name="value">The value whose data is to be converted to a string.</param>
        /// <returns>A new Value<string> containing the string representation of the input value's data.</returns>
        public static Value<string> ToString(Value<T> value)
        {
            return new Value<string>(value.Data?.ToString() ?? string.Empty);
        }
       /// <summary>
       /// Determines whether the specified object is equal to the current Value<T> instance.
       /// </summary>
       /// <remarks>Equality is determined by comparing the underlying data of the Value<T> instances. This
       /// method overrides Object.Equals.</remarks>
       /// <param name="obj">The object to compare with the current Value<T> instance.</param>
       /// <returns>true if the specified object is a Value<T> and its data is equal to the data of the current instance;
       /// otherwise, false.</returns>
        public override bool Equals(object? obj)
        {
            if (obj is Value<T> other)
            {
                return EqualityComparer<T>.Default.Equals(Data, other.Data);
            }
            return false;
        }
       /// <summary>
       /// Serves as the default hash function for the object.
       /// </summary>
       /// <remarks>The hash code is derived from the value of the Data property. Objects that are
       /// considered equal should return the same hash code.</remarks>
       /// <returns>A 32-bit signed integer hash code representing the current object.</returns>
        public override int GetHashCode()
        {
            return Data is null ? 0 : Data.GetHashCode();
        }
        /// <summary>
        /// Returns a string that represents the value of the Data property.
        /// </summary>
        /// <returns>A string representation of the Data property, or an empty string if Data is null.</returns>
        public override string ToString()
        {
            return Data?.ToString() ?? string.Empty;
        }
    }
}

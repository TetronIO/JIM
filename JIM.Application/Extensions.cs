namespace JIM.Application
{
    public static class Extensions
    {
        /// <summary>
        /// Picks a random item from a collection of values, where the likelihood on which item is selected depends 
        /// on the weight of the item in the collection.
        /// 
        /// Usage: variable.RandomElementByWeight(x => x.Weight);
        /// </summary>
        public static T? RandomElementByWeight<T>(this IEnumerable<T> sequence, Func<T, float> weightSelector)
        {
            var enumerable = sequence as T[] ?? sequence.ToArray();
            var totalWeight = enumerable.Sum(weightSelector);
            // The weight we are after...
            var itemWeightIndex = (float)new Random().NextDouble() * totalWeight;
            float currentWeightIndex = 0;

            foreach (var item in from weightedItem in enumerable select new { Value = weightedItem, Weight = weightSelector(weightedItem) })
            {
                currentWeightIndex += item.Weight;

                // If we've hit or passed the weight we are after for this item then it's the one we want....
                if (currentWeightIndex > itemWeightIndex)
                    return item.Value;
            }

            return default;
        }
    }
}

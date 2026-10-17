using UnityEngine.UIElements;

namespace GameOfLife
{
    sealed class LifeNumber : VisualElement
    {
        static readonly string[] Numerals = { "0", "1", "2", "3", "4", "5", "6", "7", "8", "9" };
        readonly Label[] digits = new Label[19];
        readonly int[] values = new int[19];
        int visibleDigits;

        public LifeNumber()
        {
            pickingMode = PickingMode.Ignore;
            style.flexDirection = FlexDirection.Row;
            style.flexShrink = 0;
            for (int i = 0; i < digits.Length; i++)
            {
                var digit = new Label("0") { pickingMode = PickingMode.Ignore };
                digit.style.marginLeft = 0;
                digit.style.marginRight = 0;
                digit.style.marginTop = 0;
                digit.style.marginBottom = 0;
                digit.style.paddingLeft = 0;
                digit.style.paddingRight = 0;
                digit.style.paddingTop = 0;
                digit.style.paddingBottom = 0;
                digit.style.display = DisplayStyle.None;
                digits[i] = digit;
                values[i] = 0;
                Add(digit);
            }
            SetValue(0);
        }

        public void SetValue(long value)
        {
            int count = 0;
            do
            {
                int index = digits.Length - 1 - count;
                int numeral = (int)(value % 10);
                if (values[index] != numeral)
                {
                    values[index] = numeral;
                    digits[index].text = Numerals[numeral];
                }
                count++;
                value /= 10;
            } while (value != 0);
            if (count > visibleDigits)
                for (int i = digits.Length - count; i < digits.Length - visibleDigits; i++)
                    digits[i].style.display = DisplayStyle.Flex;
            else
                for (int i = digits.Length - visibleDigits; i < digits.Length - count; i++)
                    digits[i].style.display = DisplayStyle.None;
            visibleDigits = count;
        }
    }
}

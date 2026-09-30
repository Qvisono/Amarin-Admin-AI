using System.Windows;
using System.Windows.Controls;
using Amarin.Core;

namespace Amarin.UI
{
    /// <summary>
    /// Цена открытого чата целиком (E3): чип в ряду композера, разбивка — в подсказке.
    /// </summary>
    /// <remarks>
    /// Сумма считается на месте, а не хранится: цены дописываются брошенными задачами (заголовок,
    /// сводка) уже после ответа, и хранимое число отставало бы. Проход по сообщениям дешёвый —
    /// это сложение, а подсказка с разбивкой строится, только когда её открывают.
    /// </remarks>
    public partial class MainWindow
    {
        private void UpdateChatCostChip()
        {
            if (ChatCostChip is null)
            {
                return;
            }

            var total = ChatCost.Total(_session);
            if (total <= 0m)
            {
                ChatCostChip.Visibility = Visibility.Collapsed;
                return;
            }

            ChatCostText.Text = ChatFormat.Cost(new VeniceCost { Usd = total, HasData = true });
            ChatCostChip.ToolTip ??= CostBreakdownTooltip.CreateEmpty();
            ChatCostChip.Visibility = Visibility.Visible;
        }

        private void ChatCostChip_ToolTipOpening(object sender, ToolTipEventArgs e)
        {
            if (ChatCostChip.ToolTip is ToolTip tip)
            {
                CostBreakdownTooltip.Fill(tip, ChatCost.Breakdown(_session));
            }
        }
    }
}

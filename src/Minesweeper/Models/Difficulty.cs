namespace Minesweeper.Models;

public record Difficulty(string Name, int Rows, int Cols, int MineCount)
{
    public static readonly Difficulty Beginner = new("初级", 9, 9, 10);
    public static readonly Difficulty Intermediate = new("中级", 16, 16, 40);
    public static readonly Difficulty Expert = new("高级", 16, 30, 99);

    public static readonly Difficulty[] All = [Beginner, Intermediate, Expert];
}

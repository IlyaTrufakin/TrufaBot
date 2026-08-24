namespace TrufaBot.Domain.Entities;

public class Person
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = "Семья"; // Семья, Друзья, Коллеги, Знакомые
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<PersonFace> Faces { get; set; } = new List<PersonFace>();
}

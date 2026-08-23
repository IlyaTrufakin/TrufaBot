namespace TrufaBot.Domain.Entities;

public class ClassificationCategory
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string TitleRu { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;

    public ICollection<MediaClassification> Classifications { get; set; } = new List<MediaClassification>();
}

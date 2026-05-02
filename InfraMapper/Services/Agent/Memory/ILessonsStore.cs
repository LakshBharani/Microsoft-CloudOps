namespace InfraMapper.Services.Agent.Memory;

public interface ILessonsStore
{
    void Write(Lesson lesson);
    IReadOnlyList<Lesson> Query(string[] resourceTypes);
}

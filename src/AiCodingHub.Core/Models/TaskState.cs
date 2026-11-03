namespace AiCodingHub.Core.Models;

/// <summary>
/// Derived activity state of the monitored Claude session, mapped from the latest
/// transcript event (see FR-8). Sub-states are inferred from event/tool kinds until the
/// hook channel lands in v1.
/// </summary>
public enum TaskState
{
    Idle,
    Working,
    Thinking,
    Generating,
    CallingTool,
    Searching,
    ReadingFile,
    WritingFile,
    Error
}

namespace Dexicon.Core.Documents;

/// <summary>
/// A document could not be attached under the name asked for, because another document in the corpus
/// has it. An <see cref="ArgumentException"/>, so a batch upload lists the file and goes on with the rest.
/// </summary>
public sealed class NameTakenException(string message, string paramName) : ArgumentException(message, paramName);

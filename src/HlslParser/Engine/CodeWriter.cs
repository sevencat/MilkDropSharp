using System.Text;

namespace HlslParser;

/// <summary>
/// Port of CodeWriter.cpp. Handles indentation and (optionally) #line markers.
/// Note: the C++ implementation never enables <c>m_writeLines</c>; the flag is
/// kept for parity, and defaults to false.
/// </summary>
public sealed class CodeWriter
{
    private readonly StringBuilder _buffer = new();
    private readonly bool _writeFileNames;

    private int _currentLine;
    private string _currentFileName;
    private int _spacesPerIndent;
    private bool _writeLines;

    public CodeWriter(bool writeFileNames = true)
    {
        _currentLine = 1;
        _currentFileName = null;
        _spacesPerIndent = 4;
        _writeLines = false;
        _writeFileNames = writeFileNames;
    }

    /// <summary>Parity with the C++ field; not exposed through any public setter there either.</summary>
    internal bool WriteLines
    {
        get => _writeLines;
        set => _writeLines = value;
    }

    public void BeginLine(int indent, string fileName = null, int lineNumber = -1)
    {
        if (_writeLines)
        {
            bool outputLine = false;
            bool outputFile = false;

            if (fileName != null && !String_.Equal(_currentFileName, fileName))
            {
                _currentFileName = fileName;
                fileName = _currentFileName;
                outputFile = true;
            }
            if (lineNumber != -1 && _currentLine != lineNumber)
            {
                _currentLine = lineNumber;
                outputLine = true;
            }
            if (outputLine || outputFile)
            {
                _buffer.Append(CPrintf.Format("#line %d", lineNumber));
                if (outputFile && _writeFileNames)
                {
                    _buffer.Append(" \"");
                    _buffer.Append(fileName);
                    _buffer.Append("\"\n\n");
                }
                else
                {
                    _buffer.Append("\n\n");
                }
            }
        }

        AppendIndent(indent);
    }

    public void EndLine(string text = null)
    {
        if (text != null)
        {
            _buffer.Append(text);
        }
        _buffer.Append('\n');
        ++_currentLine;
    }

    public void Write(string format, params object[] args)
    {
        _buffer.Append(CPrintf.Format(format, args));
    }

    public void WriteLine(int indent, string format, params object[] args)
    {
        AppendIndent(indent);
        _buffer.Append(CPrintf.Format(format, args));
        EndLine();
    }

    public void WriteLineTagged(int indent, string fileName, int lineNumber, string format, params object[] args)
    {
        BeginLine(indent, fileName, lineNumber);
        _buffer.Append(CPrintf.Format(format, args));
        EndLine();
    }

    public string GetResult()
    {
        return _buffer.ToString();
    }

    public void Reset()
    {
        _buffer.Clear();
    }

    private void AppendIndent(int indent)
    {
        int spaces = indent * _spacesPerIndent;
        for (int i = 0; i < spaces; ++i)
        {
            _buffer.Append(' ');
        }
    }
}

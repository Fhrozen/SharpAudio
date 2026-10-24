namespace SharpAudio.Api.Services.Pnc;

/// <summary>One entry of a SentencePiece vocab. Pieces are kept in file order, which is vocab order:
/// the unigram segmentation tie-breaks equal scores in that order, so the order is part of behaviour.</summary>
public readonly record struct SentencePiecePiece(string Piece, float Score, SentencePiecePieceType Type);

/// <summary>SentencePiece piece type as written in the model file. The file this model ships carries a
/// type on only four entries, so the tokenizer locates special pieces by name and uses the type only to
/// skip pieces that can never match input text.</summary>
public enum SentencePiecePieceType
{
    Control = 1,
    Unknown = 2,
    Byte = 3,
    Normal = 4,
}

/// <summary>
/// Minimal reader for the binary SentencePiece <c>.model</c> format, which is a protobuf message with a
/// repeated <c>parts</c> field (1) and a normalizer spec (2). Only the pieces are read: the spec this
/// model ships has no rule tables, so normalization is applied by the tokenizer itself.
/// </summary>
public static class SentencePieceModelReader
{
    private const int PartsField = 1;

    public static IReadOnlyList<SentencePiecePiece> Read(string modelPath)
    {
        var bytes = File.ReadAllBytes(modelPath);
        var pieces = new List<SentencePiecePiece>();

        foreach (var (field, wireType, value) in ReadFields(bytes))
        {
            if (field == PartsField && wireType == 2)
            {
                pieces.Add(ReadPiece(value));
            }
        }

        return pieces;
    }

    private static SentencePiecePiece ReadPiece(byte[] message)
    {
        string piece = string.Empty;
        float score = 0f;
        var type = SentencePiecePieceType.Normal;

        foreach (var (field, wireType, value) in ReadFields(message))
        {
            switch (field)
            {
                case 1:
                    piece = System.Text.Encoding.UTF8.GetString(value);
                    break;
                case 2:
                    score = BitConverter.ToSingle(value, 0);
                    break;
                case 3:
                    type = (SentencePiecePieceType)(int)DecodeVarint(value);
                    break;
            }
        }

        return new SentencePiecePiece(piece, score, type);
    }

    /// <summary>Yields (field number, wire type, payload) for every field in a protobuf message.
    /// Varint fields carry the value itself; fixed32 fields carry the 4 bytes; length-delimited fields
    /// carry the decoded bytes.</summary>
    private static IEnumerable<(int Field, int WireType, byte[] Value)> ReadFields(byte[] message)
    {
        int position = 0;

        while (position < message.Length)
        {
            int key = (int)ReadVarint(message, ref position);
            int wireType = key & 7;

            switch (wireType)
            {
                case 0:
                    int valueStart = position;
                    ReadVarint(message, ref position);
                    yield return (key >> 3, wireType, message[valueStart..position]);
                    break;
                case 1:
                    yield return (key >> 3, wireType, message[position..(position + 8)]);
                    position += 8;
                    break;
                case 2:
                    int length = (int)ReadVarint(message, ref position);
                    yield return (key >> 3, wireType, message[position..(position + length)]);
                    position += length;
                    break;
                case 5:
                    yield return (key >> 3, wireType, message[position..(position + 4)]);
                    position += 4;
                    break;
                default:
                    throw new InvalidDataException($"Unsupported protobuf wire type {wireType} in SentencePiece model.");
            }
        }
    }

    private static ulong DecodeVarint(byte[] bytes)
    {
        int position = 0;
        return ReadVarint(bytes, ref position);
    }

    private static ulong ReadVarint(byte[] message, ref int position)
    {
        ulong result = 0;
        int shift = 0;

        while (position < message.Length)
        {
            byte b = message[position++];
            result |= (ulong)(b & 0x7F) << shift;

            if (b < 0x80)
            {
                return result;
            }

            shift += 7;
        }

        throw new InvalidDataException("Truncated varint in SentencePiece model.");
    }
}

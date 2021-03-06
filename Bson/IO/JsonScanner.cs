﻿/* Copyright 2010-2011 10gen Inc.
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
* http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

using MongoDB.Bson;

namespace MongoDB.Bson.IO {
    /// <summary>
    /// A static class that represents a JSON scanner.
    /// </summary>
    public static class JsonScanner {
        #region public static methods
        /// <summary>
        /// Gets the next JsonToken from a JsonBuffer.
        /// </summary>
        /// <param name="buffer">The buffer.</param>
        /// <returns>The next token.</returns>
        public static JsonToken GetNextToken(
            JsonBuffer buffer
        ) {
            // skip leading whitespace
            var c = buffer.Read();
            while (c != -1 && char.IsWhiteSpace((char) c)) {
                c = buffer.Read();
            }
            if (c == -1) {
                return new JsonToken(JsonTokenType.EndOfFile, "<eof>");
            }

            // leading character determines token type
            switch (c) {
                case '{': return new JsonToken(JsonTokenType.BeginObject, "{");
                case '}': return new JsonToken(JsonTokenType.EndObject, "}");
                case '[': return new JsonToken(JsonTokenType.BeginArray, "[");
                case ']': return new JsonToken(JsonTokenType.EndArray, "]");
                case ':': return new JsonToken(JsonTokenType.Colon, ":");
                case ',': return new JsonToken(JsonTokenType.Comma, ",");
                case '"': return GetStringToken(buffer);
                case '/': return GetRegularExpressionToken(buffer);
                default:
                    if (c == '-' || char.IsDigit((char) c)) {
                        return GetNumberToken(buffer, c);
                    } else if (c == '$' || char.IsLetter((char) c)) {
                        return GetUnquotedStringToken(buffer);
                    } else {
                        buffer.UnRead(c);
                        throw new FileFormatException(FormatMessage("Invalid JSON input", buffer, buffer.Position));
                    }
            }
        }
        #endregion

        #region private methods
        private static string FormatMessage(
            string message,
            JsonBuffer buffer,
            int start
        ) {
            var length = 20;
            string snippet;
            if (buffer.Position + length >= buffer.Length) {
                snippet = buffer.Substring(start);
            } else {
                snippet = buffer.Substring(start, length) + "...";
            }
            return string.Format("{0}: '{1}'", message, snippet);
        }

        private static JsonToken GetNumberToken(
            JsonBuffer buffer,
            int c // first character
        ) {
            // leading digit or '-' has already been read
            var start = buffer.Position - 1;
            NumberState state;
            switch (c) {
                case '-': state = NumberState.SawLeadingMinus; break;
                case '0': state = NumberState.SawLeadingZero; break;
                default: state = NumberState.SawIntegerDigits; break;
            }
            var type = JsonTokenType.Int64; // assume integer until proved otherwise

            while (true) {
                c = buffer.Read();
                switch (state) {
                    case NumberState.SawLeadingMinus:
                        switch (c) {
                            case '0':
                                state = NumberState.SawLeadingZero; 
                                break;
                            default:
                                if (char.IsDigit((char) c)) {
                                    state = NumberState.SawIntegerDigits;
                                } else {
                                    state = NumberState.Invalid;
                                }
                                break;
                        }
                        break;
                    case NumberState.SawLeadingZero:
                        switch (c) {
                            case '.':
                                state = NumberState.SawDecimalPoint;
                                break;
                            case 'e':
                            case 'E':
                                state = NumberState.SawExponentLetter;
                                break;
                            case ',':
                            case '}':
                            case ']':
                            case -1:
                                state = NumberState.Done;
                                break;
                            default:
                                if (char.IsWhiteSpace((char) c)) {
                                    state = NumberState.Done;
                                } else {
                                    state = NumberState.Invalid;
                                }
                                break;
                        }
                        break;
                    case NumberState.SawIntegerDigits:
                        switch (c) {
                            case '.':
                                state = NumberState.SawDecimalPoint;
                                break;
                            case 'e':
                            case 'E':
                                state = NumberState.SawExponentLetter;
                                break;
                            case ',':
                            case '}':
                            case ']':
                            case -1:
                                state = NumberState.Done;
                                break;
                            default:
                                if (char.IsDigit((char) c)) {
                                    state = NumberState.SawIntegerDigits;
                                } else if (char.IsWhiteSpace((char) c)) {
                                    state = NumberState.Done;
                                } else {
                                    state = NumberState.Invalid;
                                }
                                break;
                        }
                        break;
                    case NumberState.SawDecimalPoint:
                        type = JsonTokenType.Double;
                        if (char.IsDigit((char) c)) {
                            state = NumberState.SawFractionDigits;
                        } else {
                            state = NumberState.Invalid;
                        }
                        break;
                    case NumberState.SawFractionDigits:
                        switch (c) {
                            case 'e':
                            case 'E':
                                state = NumberState.SawExponentLetter;
                                break;
                            case ',':
                            case '}':
                            case ']':
                            case -1:
                                state = NumberState.Done;
                                break;
                            default:
                                if (char.IsDigit((char) c)) {
                                    state = NumberState.SawFractionDigits;
                                } else if (char.IsWhiteSpace((char) c)) {
                                    state = NumberState.Done;
                                } else {
                                    state = NumberState.Invalid;
                                }
                                break;
                        }
                        break;
                    case NumberState.SawExponentLetter:
                        type = JsonTokenType.Double;
                        switch (c) {
                            case '+':
                            case '-':
                                state = NumberState.SawExponentSign;
                                break;
                            default:
                                if (char.IsDigit((char) c)) {
                                    state = NumberState.SawExponentDigits;
                                } else {
                                    state = NumberState.Invalid;
                                }
                                break;
                        }
                        break;
                    case NumberState.SawExponentSign:
                        if (char.IsDigit((char) c)) {
                            state = NumberState.SawExponentDigits;
                        } else {
                            state = NumberState.Invalid;
                        }
                        break;
                    case NumberState.SawExponentDigits:
                        switch (c) {
                            case ',':
                            case '}':
                            case ']':
                            case -1:
                                state = NumberState.Done;
                                break;
                            default:
                                if (char.IsDigit((char) c)) {
                                    state = NumberState.SawExponentDigits;
                                } else if (char.IsWhiteSpace((char) c)) {
                                    state = NumberState.Done;
                                } else {
                                    state = NumberState.Invalid;
                                }
                                break;
                        }
                        break;
                }

                switch (state) {
                    case NumberState.Done:
                        buffer.UnRead(c);
                        var lexeme = buffer.Substring(start, buffer.Position - start);
                        if (type == JsonTokenType.Double) {
                            var value = XmlConvert.ToDouble(lexeme);
                            return new DoubleJsonToken(lexeme, value);
                        } else {
                            var value = XmlConvert.ToInt64(lexeme);
                            if (value < int.MinValue || value > int.MaxValue) {
                                return new Int64JsonToken(lexeme, value);
                            } else {
                                return new Int32JsonToken(lexeme, (int) value);
                            }
                        }
                    case NumberState.Invalid:
                        throw new FileFormatException(FormatMessage("Invalid JSON number", buffer, start));
                }
            }
        }

        private static JsonToken GetRegularExpressionToken(
            JsonBuffer buffer
        ) {
            // opening slash has already been read
            var start = buffer.Position - 1;
            var state = RegularExpressionState.InPattern;
            while (true) {
                var c = buffer.Read();
                switch (state) {
                    case RegularExpressionState.InPattern:
                        switch (c) {
                            case '/': state = RegularExpressionState.InOptions; break;
                            case '\\': state = RegularExpressionState.InEscapeSequence; break;
                            default: state = RegularExpressionState.InPattern; break;
                        }
                        break;
                    case RegularExpressionState.InEscapeSequence:
                        state = RegularExpressionState.InPattern;
                        break;
                    case RegularExpressionState.InOptions:
                        switch (c) {
                            case 'g':
                            case 'i':
                            case 'm':
                                state = RegularExpressionState.InOptions;
                                break;
                            case ',':
                            case '}':
                            case ']':
                            case -1:
                                state = RegularExpressionState.Done;
                                break;
                            default:
                                if (char.IsWhiteSpace((char) c)) {
                                    state = RegularExpressionState.Done;
                                } else {
                                    state = RegularExpressionState.Invalid;
                                }
                                break;
                        }
                        break;
                }

                switch (state) {
                    case RegularExpressionState.Done:
                        buffer.UnRead(c);
                        var lexeme = buffer.Substring(start, buffer.Position - start);
                        var regex = BsonRegularExpression.Create(lexeme);
                        return new RegularExpressionJsonToken(lexeme, regex);
                    case RegularExpressionState.Invalid:
                        throw new FileFormatException(FormatMessage("Invalid JSON regular expression", buffer, start));
                }
            }
        }

        private static JsonToken GetStringToken(
            JsonBuffer buffer
        ) {
            // opening quote has already been read
            var start = buffer.Position - 1;
            var sb = new StringBuilder();
            while (true) {
                var c = buffer.Read();
                switch (c) {
                    case '\\':
                        c = buffer.Read();
                        switch (c) {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                var u1 = buffer.Read();
                                var u2 = buffer.Read();
                                var u3 = buffer.Read();
                                var u4 = buffer.Read();
                                if (u4 != -1) {
                                    var hex = new string(new char[] { (char) u1, (char) u2, (char) u3, (char) u4 });
                                    var n = Convert.ToInt32(hex, 16);
                                    sb.Append((char) n);
                                }
                                break;
                            default:
                                if (c != -1) {
                                    var message = string.Format("Invalid escape sequence in JSON string: '\\{0}'", (char) c);
                                    throw new FileFormatException(message);
                                }
                                break;
                        }
                        break;
                    case '"':
                        var lexeme = buffer.Substring(start, buffer.Position - start);
                        return new StringJsonToken(JsonTokenType.String, lexeme, sb.ToString());
                    default:
                        if (c != -1) {
                            sb.Append((char) c);
                        }
                        break;
                }
                if (c == -1) {
                    throw new FileFormatException(FormatMessage("End of file in JSON string", buffer, start));
                }
            }
        }

        private static JsonToken GetTenGenDate(
            JsonBuffer buffer,
            int start
        ) {
            var firstDigit = buffer.Position;
            while (true) {
                var c = buffer.Read();
                if (c == ')') {
                    var lexeme = buffer.Substring(start, buffer.Position - start);
                    var digits = buffer.Substring(firstDigit, buffer.Position - firstDigit - 1);
                    var ms = XmlConvert.ToInt64(digits);
                    var value = BsonConstants.UnixEpoch.AddMilliseconds(ms);
                    return new DateTimeJsonToken(lexeme, value);
                }
                if (c == -1 || !char.IsDigit((char) c)) {
                    throw new FileFormatException(FormatMessage("Invalid JSON Date value", buffer, start));
                }
            }
        }

        private static JsonToken GetTenGenInt64(
            JsonBuffer buffer,
            int start
        ) {
            var firstDigit = buffer.Position;
            while (true) {
                var c = buffer.Read();
                if (c == ')') {
                    var lexeme = buffer.Substring(start, buffer.Position - start);
                    var digits = buffer.Substring(firstDigit, buffer.Position - firstDigit - 1);
                    var value = XmlConvert.ToInt64(digits);
                    return new Int64JsonToken(lexeme, value);
                }
                if (c == -1 || !char.IsDigit((char) c)) {
                    throw new FileFormatException(FormatMessage("Invalid JSON NumberLong value", buffer, start));
                }
            }
        }

        private static JsonToken GetTenGenObjectId(
            JsonBuffer buffer,
            int start
        ) {
            var c = buffer.Read();
            if (c != '"') {
                throw new FileFormatException(FormatMessage("Invalid JSON ObjectId value", buffer, start));
            }
            var firstHexDigit = buffer.Position;
            c = buffer.Read();
            while (c != '"') {
                if (
                    !char.IsDigit((char) c) &&
                    !(c >= 'a' && c <= 'f') &&
                    !(c >= 'A' && c <= 'F')
                ) {
                    throw new FileFormatException(FormatMessage("Invalid JSON ObjectId value", buffer, start));
                }
                c = buffer.Read();
            }
            var count = buffer.Position - 1 - firstHexDigit;
            if (count != 24) {
                throw new FileFormatException(FormatMessage("Invalid JSON ObjectId value", buffer, start));
            }
            var hexDigits = buffer.Substring(firstHexDigit, count);
            var objectId = ObjectId.Parse(hexDigits);
            c = buffer.Read();
            if (c != ')') {
                throw new FileFormatException(FormatMessage("Invalid JSON ObjectId value", buffer, start));
            }
            var lexeme = buffer.Substring(start, buffer.Position - start);
            return new ObjectIdJsonToken(lexeme, objectId);
        }

        private static JsonToken GetUnquotedStringToken(
            JsonBuffer buffer
        ) {
            // opening letter or $ has already been read
            var start = buffer.Position - 1;
            string lexeme;
            while (true) {
                var c = buffer.Read();
                switch (c) {
                    case ':':
                    case ',':
                    case '}':
                    case ']':
                    case -1:
                        buffer.UnRead(c);
                        lexeme = buffer.Substring(start, buffer.Position - start);
                        return new StringJsonToken(JsonTokenType.UnquotedString, lexeme, lexeme);
                    default:
                        if (c == '$' || char.IsLetterOrDigit((char) c)) {
                            // continue
                        } else if (char.IsWhiteSpace((char) c)) {
                            buffer.UnRead(c);
                            lexeme = buffer.Substring(start, buffer.Position - start);
                            return new StringJsonToken(JsonTokenType.UnquotedString, lexeme, lexeme);
                        } else {
                            if (c == '(') {
                                var value = buffer.Substring(start, buffer.Position - 1 - start);
                                switch (value) {
                                    case "Date": return GetTenGenDate(buffer, start);
                                    case "NumberLong": return GetTenGenInt64(buffer, start);
                                    case "ObjectId": return GetTenGenObjectId(buffer, start);
                                }
                            }
                            throw new FileFormatException(FormatMessage("Invalid JSON unquoted string", buffer, start));
                        }
                        break;
                }
            }
        }
        #endregion

        #region nested types
        private enum NumberState {
            SawLeadingMinus,
            SawLeadingZero,
            SawIntegerDigits,
            SawDecimalPoint,
            SawFractionDigits,
            SawExponentLetter,
            SawExponentSign,
            SawExponentDigits,
            Done,
            Invalid
        }

        private enum RegularExpressionState {
            InPattern,
            InEscapeSequence,
            InOptions,
            Done,
            Invalid
        }
        #endregion
    }
}

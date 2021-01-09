﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace LeiKaiFeng.Http
{

    //从索引0-UsedOffset是没有使用的字节数
    //从索引UsedOffset-ReadOffset是已经读取的字节数
    //从索引ReadOffset-MaxOffset是可以读取的字节数
    //没有实现为一个环形缓冲区，但会将已经读取的字节左移

    public sealed partial class MHttpStream
    {
        public static async Task<T> CreateTimeOutTaskAsync<T>(Task<T> task, TimeSpan timeSpan, Action timeOutAction, Action completedAction, Action exceptionOrTimeOutResultAction)
        {

            Task t = await Task.WhenAny(task, Task.Delay(timeSpan)).ConfigureAwait(false);

            if (object.ReferenceEquals(t, task))
            {
                try
                {

                    T value = await task.ConfigureAwait(false);

                    completedAction();

                    return value;
                }
                catch
                {
                    exceptionOrTimeOutResultAction();

                    throw;
                }


            }
            else
            {
                timeOutAction();

                try
                {

                    var value = await task.ConfigureAwait(false);

                    exceptionOrTimeOutResultAction();

                    return value;
                }
                catch (Exception e)
                {
                    exceptionOrTimeOutResultAction();

                    throw new OperationCanceledException(string.Empty, e);
                }

            }

        }


        static readonly byte[] s_mark = Encoding.UTF8.GetBytes("\r\n");

        static readonly byte[] s_tow_mark = Encoding.UTF8.GetBytes("\r\n\r\n");

        const int SIZE = 8192;

        const int MAX_SIZE = 65536;

        readonly Socket m_socket;

        readonly Stream m_stream;
       
        readonly byte[] m_buffer;

        int m_read_offset;

        int m_used_offset;

        int CanReadSize => m_buffer.Length - m_read_offset;

        int CanUsedSize => m_read_offset - m_used_offset;

        public MHttpStream(Socket socket, Stream stream)
        {
            m_socket = socket;

            m_stream = stream;

            m_buffer = new byte[MAX_SIZE];

            m_read_offset = 0;

            m_used_offset = 0;
        }


        void Move()
        {
            int used_size = CanUsedSize;

            if (used_size >= SIZE)
            {
                throw new MHttpException("无法在有限的缓冲区中找到协议的边界，请增大缓冲区重试");
            }
            else
            {
                Buffer.BlockCopy(m_buffer, m_used_offset, m_buffer, 0, used_size);



                m_read_offset = used_size;

                m_used_offset = 0;
            }
        }

        async Task ReadAsync()
        {

            Move();

            int n = await m_stream.ReadAsync(m_buffer, m_read_offset, CanReadSize).ConfigureAwait(false);


            if (n == 0)
            {
                throw new IOException("协议未完整读取");
            }
            else
            {
                m_read_offset += n;
            }

        }

        bool GetChunkedLength(out int length)
        {
            

            int used_size = CanUsedSize;

            int index = Pf.FirstIndex(m_buffer, m_used_offset, used_size, s_mark);

            if (index == -1)
            {
                length = default;

                return false;
            }
            else
            {
                int length_s_size = index - m_used_offset;

                string s = Encoding.UTF8.GetString(m_buffer, m_used_offset, length_s_size);

                length = int.Parse(s, System.Globalization.NumberStyles.AllowHexSpecifier);

                if (length < 0)
                {
                    throw new MHttpException("Chunked Length Is 0");
                }
                else
                {

                    m_used_offset = (index + s_mark.Length);

                    return true;
                }
            }
        }

        int Copy(byte[] buffer, int offset, int size)
        {
            new ArraySegment<byte>(buffer, offset, size);

            return Copy(size, (souBuffer, souOffset, usedSize) => Buffer.BlockCopy(souBuffer, souOffset, buffer, offset, usedSize));
        }

        int Copy(MemoryStream stream, int size)
        {
            return Copy(size, (buffer, offset, usedSize) => stream.Write(buffer, offset, usedSize));
        }

        int Copy(int size, Action<byte[], int, int> func)
        {
            int used_size = CanUsedSize;

            if (used_size < size)
            {
                
                func(m_buffer, m_used_offset, used_size);

                m_used_offset += used_size;

                return used_size;
            }
            else
            {
                func(m_buffer, m_used_offset, size);

                m_used_offset += size;

                return size;
            }
        }


        async Task CopyChunkedAsync(MemoryStream stream, int size)
        {
            size = checked(size + s_mark.Length);

            while (true)
            {
                int n = Copy(stream, size);

                size -= n;

                if (size == 0)
                {
                    //ChuckChunkedEnd(stream);

                    stream.Position -= s_mark.Length;

                    return;
                }
                else
                {
                    await ReadAsync().ConfigureAwait(false);
                }
            }

        }

        static void ChuckChunkedEnd(MemoryStream stream)
        {
            byte[] buffer = stream.GetBuffer();

            if (!Pf.EndWith(buffer, checked((int)stream.Position), s_mark))
            {
                throw new MHttpException("Chunked数据的结尾标记错误");
            }
        }

        bool FindHeaders(out ArraySegment<byte> buffer)
        {
            
            int usedSize = CanUsedSize;

            int index = Pf.FirstIndex(m_buffer, m_used_offset, usedSize, s_tow_mark);

            if (index == -1)
            {
                buffer = default;

                return false;
            }
            else
            {
                index += s_tow_mark.Length;

                int offset = m_used_offset;

                int size = index - m_used_offset;

                m_used_offset = index;

                buffer = new ArraySegment<byte>(m_buffer, offset, size);

                return true;
            }

        }

        internal async Task<T> ReadHeadersAsync<T>(Func<ArraySegment<byte>, T> func)
        {

            while (true)
            {

                if (FindHeaders(out var buffer))
                {
                    return func(buffer);
                }
                else
                {
                    await ReadAsync().ConfigureAwait(false);
                }
            }
        }

        async Task ReadByteArrayAsync(int size, Func<byte[], Task> func)
        {
            byte[] buffer = new byte[size];

            int offset = 0;

            while (true)
            {
                int n = await this.ReadAsync(buffer, offset, buffer.Length - offset).ConfigureAwait(false);

                if (n == 0)
                {
                    throw new IOException();
                }

                offset += n;

                if (offset == buffer.Length)
                {
                    await func(buffer).ConfigureAwait(false);

                    return;
                }
            }
        }

        internal async Task ReadByteArrayContentAsync(int maxContentSize, Func<MemoryStream, Task> func)
        {
            MemoryStream memoryStream = new MemoryStream();
            byte[] buffer = new byte[4096];
            while (true)
            {
                int n = await this.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);

                if (n == 0)
                {
                    //memoryStream.SetLength(memoryStream.Position);

                    memoryStream.Position = 0;

                    await func(memoryStream).ConfigureAwait(false);

                    return;
                }

                memoryStream.Write(buffer, 0, n);

                if(memoryStream.Position > maxContentSize)
                {
                    throw new MHttpException("内容长度大于限制长度");
                }
            }
        }

        internal Task ReadByteArrayContentAsync(int length, int maxContentSize, Func<byte[], Task> func)
        {
            if (length > maxContentSize)
            {
                throw new MHttpException("内容长度大于限制长度");
            }
            else if (length == 0)
            {


                return func(Array.Empty<byte>());
            }
            else
            {
                return this.ReadByteArrayAsync(length, func);
            }      
        }

        internal async Task ReadChunkedContentAsync(int maxContentSize, Func<MemoryStream, Task> func)
        {
            MemoryStream stream = new MemoryStream();

            while (true)
            {


                if (!GetChunkedLength(out int length))
                {
                    await ReadAsync().ConfigureAwait(false);
                }
                else
                {
                    long sumLength = checked(stream.Position + length);
                    
                    if (sumLength > maxContentSize)
                    {
                        throw new MHttpException("内容长度大于限制长度");
                    }


                    await CopyChunkedAsync(stream, length).ConfigureAwait(false);

                    if (length == 0)
                    {
                        //stream.SetLength(stream.Position);

                        stream.Position = 0;

                        await func(stream).ConfigureAwait(false);

                        return;
                    }
                   
                }


            }
        }

        internal Task WriteAsync(byte[] buffer)
        {
            return this.WriteAsync(buffer, 0, buffer.Length);
        }

        internal Task WriteAsync(byte[] buffer, int offset, int size)
        {
            return m_stream.WriteAsync(buffer, offset, size);
        }

        internal Task<int> ReadAsync(byte[] buffer)
        {
            return this.ReadAsync(buffer, 0, buffer.Length);
        }


        internal Task<int> ReadAsync(byte[] buffer, int offset, int size)
        {

            if (CanUsedSize == 0)
            {
                return m_stream.ReadAsync(buffer, offset, size);
            }
            else
            {
                return Task.FromResult(Copy(buffer, offset, size));
            }
        }

        private bool m_is_close_socket = false;

        private void CloseSocket()
        {
            if (m_is_close_socket == false) 
            {
                m_is_close_socket = true;

                m_socket.Close();
            }
        }

        public void Close()
        {
            CloseSocket();

            m_stream.Close();
        }

        public void Cancel()
        {
            CloseSocket();
        }
    }

    public sealed partial class MHttpStream
    {
        static string ParseOneLine(string headers, ref int offset)
        {
            const string c_mark = "\r\n";

            int index = headers.IndexOf(c_mark, offset, StringComparison.OrdinalIgnoreCase);

            if (index == -1)
            {
                throw new MHttpException("解析HTTP头出错");
            }
            else
            {
                string s = headers.Substring(offset, index - offset);

                offset = index + c_mark.Length;

                return s;
            }

        }

        static KeyValuePair<string, string> ParseKeyValue(string s)
        {
            const string c_mark = ":";

            int index = s.IndexOf(c_mark, StringComparison.OrdinalIgnoreCase);

            if (index == -1)
            {
                throw new MHttpException("解析HTTP头出错");
            }
            else
            {
                string key = s.Substring(0, index).Trim();

                string value = s.Substring(index + c_mark.Length).Trim();

                return new KeyValuePair<string, string>(key, value);
            }
        }

        internal static KeyValuePair<string, MHttpHeaders> ParseLine(ArraySegment<byte> buffer)
        {
            string headers = Encoding.UTF8.GetString(buffer.Array, buffer.Offset, buffer.Count);

            int offset = 0;

            string first_line = ParseOneLine(headers, ref offset);


            var dic = new MHttpHeaders();

            while (true)
            {
                string s = ParseOneLine(headers, ref offset);

                if (s.Length == 0)
                {
                    return new KeyValuePair<string, MHttpHeaders>(first_line, dic);
                }
                else
                {
                    var keyValue = ParseKeyValue(s);

                    dic.Set(keyValue.Key, keyValue.Value);
                }

            }


        }


        internal static byte[] EncodeHeaders(string firstLine, Dictionary<string, string> headers)
        {
            const string c_mark = "\r\n";

            StringBuilder sb = new StringBuilder(2048);

            sb.Append(firstLine).Append(c_mark);

            foreach (var item in headers)
            {
                sb.Append(item.Key).Append(": ").Append(item.Value).Append(c_mark);
            }

            sb.Append(c_mark);

            string s = sb.ToString();

            return Encoding.UTF8.GetBytes(s);
        }

        

    }



    static class Pf
    {

        public static bool EndWith(byte[] x, int offset, byte[] y)
        {

            for (int xi = offset - 1, yi = y.Length - 1; xi >= 0 && yi >= 0; xi--, yi--)
            {
                if (y[yi] != x[xi])
                {
                    return false;
                }
            }

            return true;
        }

        static bool FirstIndex_SubFind(byte[] x, int index, byte[] y)
        {
            for (int i = 0; i < y.Length; i++)
            {
                if (x[index + i] != y[i])
                {
                    return false;
                }
            }

            return true;
        }

        public static int FirstIndex(byte[] x, int offset, int size, byte[] y)
        {
            int end_index = (size - y.Length) + 1;

            for (int index = 0; index < end_index; index++)
            {
                int n = offset + index;

                if (FirstIndex_SubFind(x, n, y))
                {
                    return n;
                }
            }

            return -1;


        }

    }

}
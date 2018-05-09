using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using GossipNet.Messages;

namespace GossipNet.Messages
{
    public interface IGossipMessageDecoder
    {
        IEnumerable<GossipMessage> Decode(Stream stream);
    }
}
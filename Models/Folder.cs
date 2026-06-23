using System;

namespace CardProgram.Models
{
    public class Folder
    {
        public string Id   { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = string.Empty;
    }
}

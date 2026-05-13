int n = int.Parse(Console.ReadLine()); // települések száma
string[] t; // kételemű tömb (splitted[0], splitted[1])
int max = 0; // a maximális távolság
int index = 0; // a maximális távolság sorszáma
int min = int.MaxValue; // a minimális távolság
for (int i = 1; i <= n; i++)
{
    t = Console.ReadLine().Split();
    if (int.Parse(t[0]) > max)
    {
        max = int.Parse(t[0]);
        index = i;
    }
    if (int.Parse(t[1]) < min)
    {
        min = int.Parse(t[1]);
        index = i;
    }
}

Console.WriteLine(index);
Console.ReadKey();
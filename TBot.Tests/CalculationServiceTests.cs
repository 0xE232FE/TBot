using Tbot.Includes;
using TBot.Ogame.Infrastructure.Enums;
using TBot.Ogame.Infrastructure.Models;

namespace TBot.Tests;

public class CalculationServiceTests {
    private readonly CalculationService _calculation = new(null, null);

    [Theory]
    [InlineData(CharacterClass.NoClass, false, false, 4)]
    [InlineData(CharacterClass.NoClass, true, false, 5)]
    [InlineData(CharacterClass.NoClass, false, true, 4)]
    [InlineData(CharacterClass.NoClass, true, true, 5)]
    [InlineData(CharacterClass.Discoverer, false, false, 6)]
    [InlineData(CharacterClass.Discoverer, true, false, 7)]
    [InlineData(CharacterClass.Discoverer, false, true, 6)]
    [InlineData(CharacterClass.Discoverer, true, true, 7)]
    public void Expedition_slots_use_Admiral_independently_of_Geologist(
        CharacterClass playerClass, bool admiral, bool geologist, int expectedSlots) {
        var researches = new Researches { Astrophysics = 16 };
        var staff = new Staff { Admiral = admiral, Geologist = geologist };

        int slots = _calculation.CalcMaxExpeditions(researches, playerClass, staff);

        Assert.Equal(expectedSlots, slots);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(8, 2)]
    [InlineData(9, 3)]
    [InlineData(15, 3)]
    [InlineData(16, 4)]
    public void Base_expedition_slots_increase_at_square_astrophysics_levels(int astrophysics, int expectedSlots) {
        Assert.Equal(expectedSlots, _calculation.CalcMaxExpeditions(astrophysics, false, false));
    }
}
